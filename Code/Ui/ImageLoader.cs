using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Godot;

namespace SpireCodex.Ui;

// Loads remote images (CDN card renders) into TextureRects off the main thread, caching
// decoded textures by URL. The download is async; the texture is created and assigned on
// the main thread via a deferred Callable, since Godot resources are not thread-safe.
internal static class ImageLoader
{
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Dictionary<string, ImageTexture> Cache = new();

    public static void Load(string url, TextureRect target)
    {
        if (Cache.TryGetValue(url, out var cached))
        {
            if (GodotObject.IsInstanceValid(target)) target.Texture = cached;
            return;
        }
        _ = LoadAsync(url, target);
    }

    private static async Task LoadAsync(string url, TextureRect target)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            Callable.From(() => Apply(url, bytes, target)).CallDeferred();
        }
        catch (Exception e)
        {
            MainFile.Logger.Info($"image load failed {url}: {e.Message}");
        }
    }

    private static void Apply(string url, byte[] bytes, TextureRect target)
    {
        var img = new Image();
        if (img.LoadWebpFromBuffer(bytes) != Error.Ok) return;
        var tex = ImageTexture.CreateFromImage(img);
        Cache[url] = tex;
        if (GodotObject.IsInstanceValid(target)) target.Texture = tex;
    }
}
