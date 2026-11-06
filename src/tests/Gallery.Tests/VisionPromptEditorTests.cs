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
    public async Task EditingSavingAndReloadingThePrompt_SendsTheEditedTextToTheEndpoint()
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
            // A parent rerender with the same saved settings must preserve the edit.
            await renderer.RenderAsync(root, Parameters(saved));
            var form = renderer.FindElement("form", null);
            await renderer.DispatchEventAsync(form.Event("onsubmit"), null, EventArgs.Empty);
        });

        var reloaded = await vault.Service.SettingsAsync();
        Assert.Equal(custom, reloaded.SystemPrompt);
        Assert.Equal(reloaded, savedCallback);
        await vault.Service.AnalyzeImageAsync(reloaded, "data:image/png;base64,example");
        Assert.NotNull(handler.Body);
        using var request = JsonDocument.Parse(handler.Body);
        var sent = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Equal(custom, sent);
        Assert.NotEqual(VisionSettings.DefaultSystemPrompt, sent);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Body = await request.Content!.ReadAsStringAsync(token);
            var content = JsonSerializer.Serialize(new { description = "A landscape.", tags = new[] { "landscape" } });
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
        public int AddRoot() => AssignRootComponentId(InstantiateComponent(typeof(LlmEndpointSettings)));
        public Task RenderAsync(int id, ParameterView parameters) => RenderRootComponentAsync(id, parameters);
        protected override void HandleException(Exception exception) => throw new InvalidOperationException("Editor rendering failed.", exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
        {
            for (int i = 0; i < renderBatch.UpdatedComponents.Count; i++) components.Add(renderBatch.UpdatedComponents.Array[i].ComponentId);
            return Task.CompletedTask;
        }
        public ElementEvents FindElement(string name, string? id)
        {
            foreach (int component in components)
            {
                var tree = GetCurrentRenderTreeFrames(component);
                for (int i = 0; i < tree.Count; i++)
                {
                    var frame = tree.Array[i];
                    if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != name) continue;
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
