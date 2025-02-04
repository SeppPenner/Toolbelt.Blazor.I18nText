﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Toolbelt.Blazor.I18nText.Interfaces;
using Toolbelt.Blazor.I18nText.Internals;

namespace Toolbelt.Blazor.I18nText
{
    public class I18nText
    {
        private readonly I18nTextOptions Options;

        private readonly bool RunningOnClientSide;

        private readonly HttpClient HttpClient;

        private readonly BlazorPathInfo BlazorPathInfo;

        private string _CurrentLanguage = "en";

        private readonly List<TextTable> TextTables = new List<TextTable>();

        private List<WeakReference<ComponentBase>> Components = new List<WeakReference<ComponentBase>>();

        private Task InitLangTask;

        private readonly IServiceProvider ServiceProvider;

        internal I18nText(Type typeOfStartUp, IServiceProvider serviceProvider, I18nTextOptions options)
        {
            this.RunningOnClientSide = RuntimeInformation.OSDescription == "web";
            if (this.RunningOnClientSide)
                this.HttpClient = serviceProvider.GetService(typeof(HttpClient)) as HttpClient;
            else
            {
                var blazorPathInfoService = serviceProvider.GetService(typeof(BlazorPathInfoService)) as BlazorPathInfoService;
                this.BlazorPathInfo = blazorPathInfoService.GetPathInfo(typeOfStartUp);
            }

            this.ServiceProvider = serviceProvider;
            this.Options = options;

            this.InitLangTask = this.Options.GetInitialLanguageAsync.Invoke(this.ServiceProvider, this.Options).ContinueWith(t =>
            {
                _CurrentLanguage = t.IsCompleted ? t.Result : "en";
            });
        }

        internal static Task<string> GetInitialLanguageAsync(IServiceProvider serviceProvider, I18nTextOptions options)
        {
            var jsRuntime = serviceProvider.GetService(typeof(IJSRuntime)) as IJSRuntime;
            return jsRuntime.InvokeAsync<string>("Toolbelt.Blazor.I18nText.initLang", options.PersistanceLevel);
        }

        internal static Task PersistCurrentLanguageAsync(IServiceProvider serviceProvider, string langCode, I18nTextOptions options)
        {
            var jsRuntime = serviceProvider.GetService(typeof(IJSRuntime)) as IJSRuntime;
            return jsRuntime.InvokeAsync<object>("Toolbelt.Blazor.I18nText.setCurrentLang", langCode, options.PersistanceLevel);
        }

        public async Task<string> GetCurrentLanguageAsync()
        {
            await EnsureInitialLangAsync();
            return _CurrentLanguage;
        }

        public async Task SetCurrentLanguageAsync(string langCode)
        {
            if (this._CurrentLanguage == langCode) return;

            await this.Options.PersistCurrentLanguageAsync?.Invoke(this.ServiceProvider, langCode, this.Options);

            this._CurrentLanguage = langCode;
            var allRefreshTasks = this.TextTables.Select(tt => tt.RefreshTableAsync());
            await Task.WhenAll(allRefreshTasks);

            SweepGarbageCollectedComponents();
            var stateHasChangedMethod = typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var cref in this.Components)
            {
                if (cref.TryGetTarget(out var component))
                {
                    stateHasChangedMethod.Invoke(component, new object[] { });
                }
            }
        }

        public Task<T> GetTextTableAsync<T>(ComponentBase component) where T : class, I18nTextFallbackLanguage, new()
        {
            SweepGarbageCollectedComponents();
            if (!this.Components.Exists(cref => cref.TryGetTarget(out var c) && c == component))
                this.Components.Add(new WeakReference<ComponentBase>(component));

            var fetchedTextTable = this.TextTables.FirstOrDefault(tt => tt.TableType == typeof(T));
            if (fetchedTextTable == null)
            {
                fetchedTextTable = new TextTable(typeof(T), t => FetchTextTableAsync<T>(t as T));
                this.TextTables.Add(fetchedTextTable);
            }
            return fetchedTextTable.GetTableAsync<T>();
        }

        private void SweepGarbageCollectedComponents()
        {
            // DEBUG: var beforeCount = this.Components.Count;
            this.Components = this.Components.Where(cref => cref.TryGetTarget(out var _)).ToList();
            // DEBUG: var afterCount = this.Components.Count;
            // DEBUG: Console.WriteLine($"SweepGarbageCollectedComponents - {(beforeCount - afterCount)} objects are sweeped. ({this.Components.Count} objects are stay.)");
        }

        private async Task<object> FetchTextTableAsync<T>(T table) where T : class, I18nTextFallbackLanguage, new()
        {
            await EnsureInitialLangAsync();

            var fallbackLanguage = (Activator.CreateInstance<T>() as I18nTextFallbackLanguage).FallBackLanguage;

            string[] splitLangCode(string lang)
            {
                var splitedLang = lang.Split('-');
                return splitedLang.Length == 1 ? new[] { lang } : new[] { lang, splitedLang[0] };
            }
            void appendLangCode(List<string> target, string[] source) { foreach (var item in source) if (!target.Contains(item)) target.Add(item); }

            var langs = new List<string>(capacity: 4);
            appendLangCode(langs, splitLangCode(this._CurrentLanguage));
            appendLangCode(langs, splitLangCode(fallbackLanguage));

            var jsonUrls = new List<string>(langs.Count * 2);
            foreach (var lang in langs)
            {
                jsonUrls.Add("content/i18ntext/" + typeof(T).FullName + "." + lang + ".json");
                jsonUrls.Add("_content/i18ntext/" + typeof(T).FullName + "." + lang + ".json");
            }

            var textMap = default(Dictionary<string, string>);
            foreach (var jsonUrl in jsonUrls)
            {
                try
                {
                    if (this.RunningOnClientSide)
                    {
                        var jsonText = await this.HttpClient.GetStringAsync(jsonUrl);
                        textMap = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonText);
                        break;
                    }
                    else
                    {
                        if (this.BlazorPathInfo == null) break;

                        var baseUri = jsonUrl.StartsWith("_") ? this.BlazorPathInfo.DistUri : this.BlazorPathInfo.WebRootUri;
                        var jsonLocalPath = new Uri(baseUri, relativeUri: jsonUrl).LocalPath;
                        if (File.Exists(jsonLocalPath))
                        {
                            var jsonText = File.ReadAllText(jsonLocalPath);
                            textMap = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonText);
                            break;
                        }
                    }
                }
                catch (JsonException) { }
                catch (HttpRequestException e) when (e.Message.Split(' ').Contains("404")) { }
            }

            var fields = typeof(T).GetFields(BindingFlags.Instance | BindingFlags.Public).Where(f => f.FieldType == typeof(string));
            if (textMap != null)
            {
                foreach (var field in fields)
                {
                    field.SetValue(table, textMap.TryGetValue(field.Name, out var text) ? text : field.Name);
                }
            }
            else foreach (var field in fields) field.SetValue(table, field.Name);

            return table;
        }

        private async Task EnsureInitialLangAsync()
        {
            var initLangTask = default(Task);
            lock (this) initLangTask = this.InitLangTask;
            if (initLangTask != null && !initLangTask.IsCompleted)
            {
                await initLangTask;
                lock (this) { this.InitLangTask?.Dispose(); this.InitLangTask = null; }
            }
        }
    }
}
