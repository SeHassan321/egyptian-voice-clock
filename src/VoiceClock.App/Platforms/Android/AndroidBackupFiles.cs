using System.Collections.Concurrent;
using Android.App;
using Android.Content;
using VoiceClock.Core.Resources;
using VoiceClock.Core.Storage;
using VoiceClock.Core.ViewModels;

namespace VoiceClock.App.Droid;

/// <summary>Bridges startActivityForResult to async/await for the system document pickers.</summary>
internal static class ActivityResults
{
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<Intent?>> Pending = new();
    private static int _nextRequestCode = 7300;

    public static Task<Intent?> StartAsync(Intent intent)
    {
        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("No visible screen to show the picker.");
        var requestCode = Interlocked.Increment(ref _nextRequestCode);
        var completion = new TaskCompletionSource<Intent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[requestCode] = completion;
        activity.StartActivityForResult(intent, requestCode);
        return completion.Task;
    }

    public static bool TryComplete(int requestCode, Result resultCode, Intent? data)
    {
        if (!Pending.TryRemove(requestCode, out var completion))
        {
            return false;
        }

        completion.TrySetResult(resultCode == Result.Ok ? data : null);
        return true;
    }
}

/// <summary>
/// Backups through the Storage Access Framework: the user chooses the location, and no storage
/// permission is needed. Sharing uses the system share sheet.
/// </summary>
public sealed class AndroidBackupFiles : IBackupFiles
{
    private static readonly string[] ZipMimeTypes = ["application/zip", "application/x-zip-compressed", "application/octet-stream"];

    public async Task<bool> SaveAsync(string suggestedFileName, Func<System.IO.Stream, CancellationToken, Task> writeContent, CancellationToken cancellationToken = default)
    {
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(VoicePackFormat.MimeType);
        intent.PutExtra(Intent.ExtraTitle, suggestedFileName);

        var result = await ActivityResults.StartAsync(intent);
        var uri = result?.Data;
        if (uri is null)
        {
            return false;
        }

        var resolver = Android.App.Application.Context.ContentResolver
            ?? throw new IOException("Content resolver unavailable.");
        await Task.Run(
            async () =>
            {
                await using var output = resolver.OpenOutputStream(uri, "w")
                    ?? throw new IOException("The chosen file cannot be written.");
                await writeContent(output, cancellationToken);
            },
            cancellationToken);
        return true;
    }

    public async Task ShareAsync(string fileName, string contentType, Func<System.IO.Stream, CancellationToken, Task> writeContent, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(FileSystem.Current.CacheDirectory, "share");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);

        await Task.Run(
            async () =>
            {
                await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                await writeContent(output, cancellationToken);
            },
            cancellationToken);

        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = Strings.Setup_Section_Backup,
            File = new ShareFile(path, contentType),
        });
    }

    public async Task<System.IO.Stream?> PickAsync(CancellationToken cancellationToken = default)
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraMimeTypes, ZipMimeTypes);

        var result = await ActivityResults.StartAsync(intent);
        var uri = result?.Data;
        if (uri is null)
        {
            return null;
        }

        var resolver = Android.App.Application.Context.ContentResolver
            ?? throw new IOException("Content resolver unavailable.");

        // Copy into memory off the UI thread; a voice pack is a few megabytes.
        return await Task.Run(
            async () =>
            {
                await using var input = resolver.OpenInputStream(uri)
                    ?? throw new IOException("The chosen file cannot be read.");
                var memory = new MemoryStream();
                await input.CopyToAsync(memory, cancellationToken);
                memory.Position = 0;
                return (System.IO.Stream)memory;
            },
            cancellationToken);
    }
}
