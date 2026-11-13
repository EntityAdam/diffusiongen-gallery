using System.Net;
using System.Text.Json;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShadCn.Blazor.Components;

#pragma warning disable BL0006 // This test deliberately dispatches real rendered input and submit events.

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class VisionPromptEditorTests
{
    [Fact]
    public async Task EditingSavingAndReloadingModelSettings_SendsTheEditedValuesToTheEndpoint()
    {
        using var handler = new CaptureHandler();
        using var client = new HttpClient(handler);
        using var vault = new TestVault(vision: new VisionManager(client));
        await vault.InitializeAsync();
        var saved = new VisionSettings { Model = "vision", SystemPrompt = "Previously saved image instructions." };
        await vault.Service.SaveSettingsAsync(saved);
        using var services = new ServiceCollection().AddLogging().AddShadCnBlazorComponents()
            .AddSingleton(vault.Service).BuildServiceProvider();
        await using var renderer = new EditorRenderer(services, services.GetRequiredService<ILoggerFactory>());
        VisionSettings? savedCallback = null;
        ParameterView Parameters(VisionSettings settings) => ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(LlmEndpointSettings.SavedSettings)] = settings,
            [nameof(LlmEndpointSettings.Saved)] = EventCallback.Factory.Create<VisionSettings>(this, value => savedCallback = value)
        });
        int root = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            int id = renderer.AddRoot();
            await renderer.RenderAsync(id, Parameters(new VisionSettings()));
            return id;
        });
        // Saved settings may arrive after the editor was first rendered.
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync(root, Parameters(saved)));
        Assert.Equal(saved.SystemPrompt, renderer.FindElement("textarea", "vision-system-prompt").Value);

        const string custom = "PROMPT_FROM_EDITOR_8721\nDescribe lighting and camera position. Return JSON with description and tags.";
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var input = renderer.FindElement("textarea", "vision-system-prompt");
            await renderer.DispatchEventAsync(input.Event("oninput"), new EventFieldInfo { ComponentId = input.ComponentId, FieldValue = custom },
                new ChangeEventArgs { Value = custom });
            foreach (var (id, value) in new[]
            {
                ("analysis-max-tokens", "16384"),
                ("text-max-tokens", "8192"), ("analysis-temperature", "0.6"), ("text-temperature", "0.4")
            })
            {
                var field = renderer.FindElement("input", id);
                await renderer.DispatchEventAsync(field.Event("onchange"),
                    new EventFieldInfo { ComponentId = field.ComponentId, FieldValue = value }, new ChangeEventArgs { Value = value });
            }
            // A parent rerender with the same saved settings must preserve the edit.
            await renderer.RenderAsync(root, Parameters(saved));
            var form = renderer.FindElement("form", null);
            await renderer.DispatchEventAsync(form.Event("onsubmit"), null, EventArgs.Empty);
        });

        var reloaded = await vault.Service.SettingsAsync();
        Assert.Equal(custom, reloaded.SystemPrompt);
        Assert.Equal(16384, reloaded.AnalysisMaxTokens);
        Assert.Equal(8192, reloaded.TextMaxTokens);
        Assert.Equal(0.6, reloaded.AnalysisTemperature);
        Assert.Equal(0.4, reloaded.TextTemperature);
        Assert.Equal(reloaded, savedCallback);
        await vault.Service.AnalyzeImageAsync(reloaded, "data:image/png;base64,example");
        Assert.NotNull(handler.Body);
        using var request = JsonDocument.Parse(handler.Body);
        var sent = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Equal(custom, sent);
        Assert.NotEqual(VisionSettings.DefaultSystemPrompt, sent);
        Assert.Equal(16384, request.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.6, request.RootElement.GetProperty("temperature").GetDouble());

        await new VisionManager(client).SuggestSetAsync(reloaded, "Series", [], []);
        using var textRequest = JsonDocument.Parse(handler.Body!);
        Assert.Equal(8192, textRequest.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.4, textRequest.RootElement.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task StructuredSettings_SaveFormatPromptTagCheckboxesAndBudgetWithoutChangingOtherPrompts()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var settings = new VisionSettings { Model = "vision", SetSuggestionSystemPrompt = "Keep set instructions" };
        using var services = new ServiceCollection().AddLogging().AddShadCnBlazorComponents()
            .AddSingleton(vault.Service).BuildServiceProvider();
        await using var renderer = new EditorRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var id = renderer.AddRoot();
            await renderer.RenderAsync(id, ParameterView.FromDictionary(new Dictionary<string, object?>
            { [nameof(LlmEndpointSettings.SavedSettings)] = settings }));
            await ToggleCheckboxAsync(renderer, "structured-analysis-enabled", true);
            Assert.Equal(Gallery.Core.Services.Processors.StructuredVision.DefaultSystemPrompt,
                renderer.FindElement("textarea", "vision-system-prompt").Value);
            var existing = renderer.FindElement(null, "vision-existing-tags");
            if (existing.Events.TryGetValue("onchange", out var change))
                await renderer.DispatchEventAsync(change, null, new ChangeEventArgs { Value = false });
            else await renderer.DispatchEventAsync(existing.Event("onclick"), null, new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
            var budget = renderer.FindElement(null, "structured-analysis-max-tokens");
            await renderer.DispatchEventAsync(budget.Event("onchange"), null, new ChangeEventArgs { Value = "16384" });
            var form = renderer.FindElement("form", null);
            await renderer.DispatchEventAsync(form.Event("onsubmit"), null, EventArgs.Empty);
        });
        var saved = await vault.Service.SettingsAsync();
        Assert.True(saved.StructuredAnalysis);
        Assert.False(saved.UseExistingTags);
        Assert.True(saved.SuggestNewTags);
        Assert.Equal(16384, saved.StructuredAnalysisMaxTokens);
        Assert.Equal(Gallery.Core.Services.Processors.StructuredVision.DefaultSystemPrompt, saved.SystemPrompt);
        Assert.Equal(settings.SetSuggestionSystemPrompt, saved.SetSuggestionSystemPrompt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwitchingModes_PreservesCustomPromptsAcrossEditsSaveReloadAndDiscard(bool initiallyStructured)
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var original = new VisionSettings { Model = "vision", StructuredAnalysis = initiallyStructured, SystemPrompt = "Original custom prompt" };
        await vault.Service.SaveSettingsAsync(original);
        using var services = new ServiceCollection().AddLogging().AddShadCnBlazorComponents()
            .AddSingleton(vault.Service).BuildServiceProvider();
        await using (var renderer = new EditorRenderer(services, services.GetRequiredService<ILoggerFactory>()))
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var id = renderer.AddRoot();
                await renderer.RenderAsync(id, Parameters(original));
                Assert.False(renderer.Editor!.HasUnsavedChanges);
                await ToggleCheckboxAsync(renderer, "structured-analysis-enabled", !initiallyStructured);
                Assert.Equal(initiallyStructured ? VisionSettings.DefaultSystemPrompt : Gallery.Core.Services.Processors.StructuredVision.DefaultSystemPrompt,
                    renderer.FindElement("textarea", "vision-system-prompt").Value);
                await EditPromptAsync(renderer, "Other mode custom prompt");
                await ToggleCheckboxAsync(renderer, "structured-analysis-enabled", initiallyStructured);
                Assert.Equal(original.SystemPrompt, renderer.FindElement("textarea", "vision-system-prompt").Value);
                // Editing a different task must not replace either image prompt.
                var tasks = renderer.FindElement(null, "prompt-task");
                await renderer.DispatchEventAsync(tasks.Event("onchange"), null, new ChangeEventArgs { Value = "set" });
                var set = renderer.FindElement("textarea", "set-suggestion-prompt");
                await renderer.DispatchEventAsync(set.Event("oninput"), null, new ChangeEventArgs { Value = "Custom set prompt" });
                await ToggleCheckboxAsync(renderer, "structured-analysis-enabled", !initiallyStructured);
                Assert.Equal("Other mode custom prompt", renderer.FindElement("textarea", "vision-system-prompt").Value);
                await renderer.RenderAsync(id, Parameters(original)); // parent rerender keeps both drafts
                var form = renderer.FindElement("form", null);
                await renderer.DispatchEventAsync(form.Event("onsubmit"), null, EventArgs.Empty);
                Assert.False(renderer.Editor.HasUnsavedChanges);
            });
        }
        var saved = await vault.Service.SettingsAsync();
        Assert.Equal(!initiallyStructured, saved.StructuredAnalysis);
        Assert.Equal("Other mode custom prompt", saved.SystemPrompt);
        Assert.Equal("Custom set prompt", saved.SetSuggestionSystemPrompt);
        Assert.Equal(initiallyStructured ? "Original custom prompt" : "Other mode custom prompt", saved.StructuredSystemPrompt);
        Assert.Equal(initiallyStructured ? "Other mode custom prompt" : "Original custom prompt", saved.LegacySystemPrompt);
        await using (var renderer = new EditorRenderer(services, services.GetRequiredService<ILoggerFactory>()))
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var id = renderer.AddRoot();
                await renderer.RenderAsync(id, Parameters(saved));
                Assert.False(renderer.Editor!.HasUnsavedChanges);
                await ToggleCheckboxAsync(renderer, "structured-analysis-enabled", initiallyStructured);
                Assert.Equal("Original custom prompt", renderer.FindElement("textarea", "vision-system-prompt").Value);
                await EditPromptAsync(renderer, "Discard this edit");
                renderer.Editor.DiscardDraft();
                await renderer.RenderAsync(id, Parameters(saved));
                Assert.False(renderer.Editor.HasUnsavedChanges);
                await ToggleCheckboxAsync(renderer, "structured-analysis-enabled", initiallyStructured);
                Assert.Equal("Original custom prompt", renderer.FindElement("textarea", "vision-system-prompt").Value);
            });
        }
        static ParameterView Parameters(VisionSettings settings) => ParameterView.FromDictionary(new Dictionary<string, object?>
        { [nameof(LlmEndpointSettings.SavedSettings)] = settings });
    }

    private static async Task ToggleCheckboxAsync(EditorRenderer renderer, string id, bool value)
    {
        var field = renderer.FindElement(null, id);
        if (field.Events.TryGetValue("onchange", out var change))
            await renderer.DispatchEventAsync(change, null, new ChangeEventArgs { Value = value });
        else await renderer.DispatchEventAsync(field.Event("onclick"), null, new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
    }
    private static Task EditPromptAsync(EditorRenderer renderer, string value)
    {
        var field = renderer.FindElement("textarea", "vision-system-prompt");
        return renderer.DispatchEventAsync(field.Event("oninput"), null, new ChangeEventArgs { Value = value });
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Body = await request.Content!.ReadAsStringAsync(token);
            var content = JsonSerializer.Serialize(new { description = "A landscape.", tags = new[] { "landscape" }, setId = "" });
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }))
            };
        }
    }

    private sealed record ElementEvents(int ComponentId, string? Value, Dictionary<string, ulong> Events)
    {
        public ulong Event(string name) => Events[name];
    }
    private sealed class EditorRenderer(IServiceProvider services, ILoggerFactory logger) : Renderer(services, logger)
    {
        private readonly HashSet<int> components = [];
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public LlmEndpointSettings? Editor { get; private set; }
        public int AddRoot() => AssignRootComponentId(Editor = (LlmEndpointSettings)InstantiateComponent(typeof(LlmEndpointSettings)));
        public Task RenderAsync(int id, ParameterView parameters) => RenderRootComponentAsync(id, parameters);
        protected override void HandleException(Exception exception) => throw new InvalidOperationException("Editor rendering failed.", exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
        {
            for (int i = 0; i < renderBatch.UpdatedComponents.Count; i++) components.Add(renderBatch.UpdatedComponents.Array[i].ComponentId);
            return Task.CompletedTask;
        }
        public ElementEvents FindElement(string? name, string? id)
        {
            foreach (int component in components)
            {
                var tree = GetCurrentRenderTreeFrames(component);
                for (int i = 0; i < tree.Count; i++)
                {
                    var frame = tree.Array[i];
                    if (frame.FrameType != RenderTreeFrameType.Element || (name is not null && frame.ElementName != name)) continue;
                    var attributes = new Dictionary<string, object?>();
                    var events = new Dictionary<string, ulong>();
                    for (int j = i + 1; j < tree.Count && tree.Array[j].FrameType == RenderTreeFrameType.Attribute; j++)
                    {
                        var attribute = tree.Array[j];
                        attributes[attribute.AttributeName] = attribute.AttributeValue;
                        if (attribute.AttributeEventHandlerId != 0) events[attribute.AttributeName] = attribute.AttributeEventHandlerId;
                    }
                    if (id is null || attributes.GetValueOrDefault("id") as string == id)
                        return new(component, attributes.GetValueOrDefault("value") as string, events);
                }
            }
            throw new InvalidOperationException($"No rendered {name} with id {id}.");
        }
    }
#pragma warning restore BL0006
}
