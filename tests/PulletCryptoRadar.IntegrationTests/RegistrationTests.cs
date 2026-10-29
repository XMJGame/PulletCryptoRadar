using System.Collections;
using PulletCryptoRadar.Core;
using PulletCryptoRadar.Infrastructure;

namespace PulletCryptoRadar.IntegrationTests;

public sealed class RegistrationTests
{
    [Fact]
    public async Task MarketSnapshotCannotObservePartiallyRegisteredUniverse()
    {
        var store = new MarketRuntimeStore();
        using var paused = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var instruments = new PausingList(paused, release);
        var registration = Task.Run(() => store.Register(instruments));
        Assert.True(paused.Wait(TimeSpan.FromSeconds(5)));
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = Task.Run(() => { readStarted.SetResult(); return store.GetAll(); });
        try
        {
            await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var completed = await Task.WhenAny(read, Task.Delay(100));
            Assert.NotSame(read, completed);
        }
        finally { release.Set(); }
        await registration.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, (await read).Count);
    }
    private sealed class PausingList(ManualResetEventSlim paused, ManualResetEventSlim release) : IReadOnlyList<Instrument>
    {
        private int _iterations;
        public int Count => 2;
        public Instrument this[int index] => new(index + 1, $"TEST{index}USDT", $"TEST{index}", DateTimeOffset.UnixEpoch, true);
        public IEnumerator<Instrument> GetEnumerator()
        {
            var pass = Interlocked.Increment(ref _iterations);
            yield return this[0];
            // Register's first pass computes active IDs; pause the second pass after the first state has been inserted.
            if (pass == 2) { paused.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); }
            yield return this[1];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
