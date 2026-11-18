using System.IO;

namespace Jargb;

internal static class LocalAccess
{
    internal static T Run<T>(string resource, Func<T> action)
    {
        using Mutex mutex = new(false, $@"Local\Jargb.{resource}");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                throw new IOException($"JARGB: доступ к {resource} занят другой операцией.");
        }
        catch (AbandonedMutexException error)
        {
            mutex.ReleaseMutex();
            throw new IOException($"JARGB: предыдущая операция с {resource} была прервана. Повторите команду.", error);
        }

        try { return action(); }
        finally { mutex.ReleaseMutex(); }
    }

    internal static void Run(string resource, Action action) => Run(resource, () =>
    {
        action();
        return true;
    });
}
