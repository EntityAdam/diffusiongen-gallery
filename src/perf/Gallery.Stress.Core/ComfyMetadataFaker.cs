using System.Text;
using System.Text.Json;

namespace Gallery.Stress.Core;

public sealed record SyntheticMetadata(string Prompt, string Workflow, string StoredMetadata);

public static class ComfyMetadataFaker
{
    public static SyntheticMetadata Create(int seed, long index, int metadataBytes, int width, int height)
    {
        var random = new Random(Seed(seed, index));
        string[] subjects = ["misty mountains", "a copper observatory", "a forest river", "an illuminated city", "a distant nebula"];
        var prompt = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["1"] = new { class_type = "CheckpointLoaderSimple", inputs = new { ckpt_name = $"synthetic-model-{random.Next(1, 5)}.safetensors" } },
            ["2"] = new { class_type = "CLIPTextEncode", inputs = new { text = $"{subjects[random.Next(subjects.Length)]}, intricate detail, cinematic light, variation {seed}-{index}", clip = new object[] { "1", 1 } } },
            ["3"] = new { class_type = "CLIPTextEncode", inputs = new { text = "blur, noise, low quality", clip = new object[] { "1", 1 } } },
            ["4"] = new { class_type = "EmptyLatentImage", inputs = new { width, height, batch_size = 1 } },
            ["5"] = new { class_type = "KSampler", inputs = new { seed = random.NextInt64(), steps = random.Next(20, 41), cfg = random.Next(5, 10), sampler_name = "euler", scheduler = "normal", denoise = 1.0, model = new object[] { "1", 0 }, positive = new object[] { "2", 0 }, negative = new object[] { "3", 0 }, latent_image = new object[] { "4", 0 } } },
            ["6"] = new { class_type = "VAEDecode", inputs = new { samples = new object[] { "5", 0 }, vae = new object[] { "1", 2 } } },
            ["7"] = new { class_type = "SaveImage", inputs = new { images = new object[] { "6", 0 }, filename_prefix = "synthetic" } }
        });
        var nodes = new List<object>();
        var paddingBytes = Math.Max(0, metadataBytes - Encoding.UTF8.GetByteCount(prompt) - 150);
        for (var n = 0; paddingBytes > 0; n++)
        {
            var length = Math.Min(1024, paddingBytes);
            var text = new string(Enumerable.Range(0, length).Select(_ => (char)('a' + random.Next(26))).ToArray());
            nodes.Add(new { id = n + 1, type = "Note", pos = new[] { random.Next(2000), random.Next(2000) }, widgets_values = new[] { text } });
            paddingBytes -= Math.Min(paddingBytes, length + 90);
        }
        var workflow = JsonSerializer.Serialize(new { version = 0.4, nodes, links = Array.Empty<object>() });
        var stored = JsonSerializer.Serialize(new { png = new[] { new { Keyword = "prompt", Value = prompt }, new { Keyword = "workflow", Value = workflow } } });
        return new(prompt, workflow, stored);
    }

    // Do not use HashCode.Combine: its salt changes between processes.
    public static int Seed(int seed, long index)
    {
        var value = unchecked((ulong)index + (uint)seed * 0x9e3779b97f4a7c15UL);
        value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
        return unchecked((int)(value ^ (value >> 31)));
    }
}
