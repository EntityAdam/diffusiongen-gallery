using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Gallery.Core.Services;

namespace Gallery.Stress;

public sealed class VaultOpenListener : IDisposable
{
    private readonly ActivityListener activities;
    private readonly MeterListener meter = new();
    private readonly ConcurrentDictionary<string, object> tags = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<double>> measurements = new();
    public IReadOnlyDictionary<string, object> Tags => tags;

    public VaultOpenListener()
    {
        activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GalleryService.VaultOpenActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                foreach (var tag in activity.TagObjects)
                    if (tag.Value is not null) tags[tag.Key] = tag.Value;
            }
        };
        ActivitySource.AddActivityListener(activities);
        meter.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == GalleryService.CatalogMeterName) listener.EnableMeasurementEvents(instrument);
        };
        meter.SetMeasurementEventCallback<long>((instrument, value, _, _) => Add(instrument.Name, value));
        meter.SetMeasurementEventCallback<double>((instrument, value, _, _) => Add(instrument.Name, value));
        meter.Start();
    }

    private void Add(string name, double value) => measurements.GetOrAdd(name, _ => new()).Enqueue(value);
    public Dictionary<string, object> Metrics() => measurements.ToDictionary(pair => pair.Key, pair =>
    {
        var sorted = pair.Value.Order().ToArray();
        return (object)new { count = sorted.Length, sum = sorted.Sum(), p50 = sorted[(int)Math.Ceiling(sorted.Length * .5) - 1], p95 = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1] };
    });
    public void Dispose() { activities.Dispose(); meter.Dispose(); }
}
