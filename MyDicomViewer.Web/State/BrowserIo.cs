using Microsoft.JSInterop;

namespace MyDicomViewer.Web.State;

/// <summary>AI のモデルの情報（ai.js から）</summary>
public sealed record ModelInfo(string Source, string Name, double Threshold, long Size, bool Pending);

/// <summary>ブラウザーとのやりとり（ファイルの受け取り・保存・キー操作・AI）。ファイルはブラウザーの中だけで扱う</summary>
public sealed class BrowserIo(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? io;
    private IJSObjectReference? files;
    private IJSObjectReference? ai;

    private async ValueTask<IJSObjectReference> Io() => io ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/io.js");

    private async ValueTask<IJSObjectReference> Files() => files ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/files.js");

    private async ValueTask<IJSObjectReference> Ai() => ai ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/ai.js");

    public async ValueTask InitFilesAsync(object dotnetRef, string dropId) => await (await Files()).InvokeVoidAsync("init", dotnetRef, dropId);

    public async ValueTask<string> FileNameAsync(int index) => await (await Files()).InvokeAsync<string>("name", index);

    public async ValueTask<byte[]?> ReadFileAsync(int index) => await (await Files()).InvokeAsync<byte[]?>("read", index);

    public async ValueTask ReleaseFilesAsync() => await (await Files()).InvokeVoidAsync("release");

    public async ValueTask<byte[]?> FetchAsync(string url) => await (await Io()).InvokeAsync<byte[]?>("fetchBytes", url);

    public async ValueTask DownloadAsync(string name, string mime, byte[] bytes) => await (await Io()).InvokeVoidAsync("download", name, mime, bytes);

    public async ValueTask DownloadDataUrlAsync(string name, string dataUrl) => await (await Io()).InvokeVoidAsync("downloadDataUrl", name, dataUrl);

    public async ValueTask ListenKeysAsync(object dotnetRef) => await (await Io()).InvokeVoidAsync("listenKeys", dotnetRef);

    public async ValueTask ClickAsync(string id) => await (await Io()).InvokeVoidAsync("clickElement", id);

    public async ValueTask<bool> IsNarrowAsync() => await (await Io()).InvokeAsync<bool>("isNarrow");

    public async ValueTask<bool> FolderPickerSupportedAsync() => await (await Io()).InvokeAsync<bool>("folderPickerSupported");

    public async ValueTask<ModelInfo?> ProbeModelAsync() => await (await Ai()).InvokeAsync<ModelInfo?>("probe");

    public async ValueTask BindModelPickerAsync(string inputId, object dotnetRef) => await (await Ai()).InvokeVoidAsync("bindPicker", inputId, dotnetRef);

    public async ValueTask ForgetModelAsync() => await (await Ai()).InvokeVoidAsync("forget");

    public async ValueTask<byte[]> RunModelAsync(byte[] tensor, int size) => await (await Ai()).InvokeAsync<byte[]>("run", TimeSpan.FromMinutes(3), tensor, size);

    public async ValueTask DisposeAsync()
    {
        foreach (var m in new[] { io, files, ai })
        {
            if (m is not null) await m.DisposeAsync();
        }
    }
}
