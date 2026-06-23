#nullable enable

using System;
using System.Threading.Tasks;

namespace ElevenLabs.Native.Tests
{
    /// <summary>
    /// Polyfill for <c>Task.WaitAsync(TimeSpan)</c>. The .NET 6+ method isn't
    /// available against Unity 6's .NET Standard 2.1 surface, so the test
    /// project ships its own extension with the same name + shape — every
    /// async assertion routes through it so a stalled task fails as a
    /// <see cref="TimeoutException"/> rather than hanging the test runner.
    /// </summary>
    internal static class TaskTimeoutExtensions
    {
        public static async Task<T> WaitAsync<T>(this Task<T> task, TimeSpan timeout)
        {
            Task winner = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            if (winner != task)
                throw new TimeoutException(
                    $"Task<{typeof(T).Name}> did not complete within {timeout}."
                );
            return await task.ConfigureAwait(false);
        }

        public static async Task WaitAsync(this Task task, TimeSpan timeout)
        {
            Task winner = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            if (winner != task)
                throw new TimeoutException($"Task did not complete within {timeout}.");
            await task.ConfigureAwait(false);
        }
    }
}
