using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.NET.Common;
using BepInExResoniteShim;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;

// using BepisResoniteWrapper;

namespace VideoShield;

[ResonitePlugin(PluginMetadata.GUID, PluginMetadata.NAME, PluginMetadata.VERSION, PluginMetadata.AUTHORS, PluginMetadata.REPOSITORY_URL)]
[BepInDependency(BepInExResoniteShim.PluginMetadata.GUID, BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BasePlugin
{
    internal static new ManualLogSource Log = null!;

    public static ConfigEntry<bool> OptIn = null!;
    public static ConfigEntry<int> OptInTimeout = null!;

    public override void Load()
    {
        Log = base.Log;

        OptIn = Config.Bind("General", "Enabled", true, "Should the plugin be enabled?");
        OptInTimeout = Config.Bind("General", "OptInTimeout", 30, "How long until the request is denied? -1 to disable");

        HarmonyInstance.PatchAll();

        Log.LogInfo($"Plugin {PluginMetadata.GUID} is loaded!");
    }

    [HarmonyPatch(typeof(VideoTextureProvider))]
    public static class Thing
    {
        private static readonly ConditionalWeakTable<VideoTextureProvider, object> Accepted = new ConditionalWeakTable<VideoTextureProvider, object>();

        [HarmonyPrefix, HarmonyPatch("LoadFromVideoServiceIntern")]
        public static bool ServicePrefix(VideoTextureProvider __instance, Uri url, CancellationToken cancellationToken, ref Task<bool> __result)
        {
            if (!OptIn.Value) return true;

            if (Accepted.Remove(__instance))
                return true;

            TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
            __result = tcs.Task;

            ShowPrompt(url, cancellationToken).ContinueWith(t =>
            {
                if (!t.Result)
                {
                    tcs.TrySetResult(false);
                    return;
                }

                try
                {
                    Accepted.Add(__instance, true);
                    MethodInfo orig = AccessTools.Method(typeof(VideoTextureProvider), "LoadFromVideoServiceIntern");
                    Task<bool> task = (Task<bool>)orig.Invoke(__instance, new object[] { url, cancellationToken })!;

                    task.ContinueWith(delegate(Task<bool> inner)
                    {
                        if (inner.IsCanceled) tcs.TrySetCanceled();
                        else if (inner.IsFaulted) tcs.TrySetException(inner.Exception);
                        else tcs.TrySetResult(inner.Result);
                    }, cancellationToken);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }, cancellationToken);

            return false;
        }

        [HarmonyPrefix, HarmonyPatch("LoadFromAsset")]
        public static bool AssetPrefix(VideoTextureProvider __instance, Uri assetURL, ref ValueTask __result)
        {
            if (!OptIn.Value) return true;

            if (Accepted.Remove(__instance))
                return true;

            TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
            __result = new ValueTask(tcs.Task);

            ShowPrompt(assetURL, CancellationToken.None).ContinueWith(t =>
            {
                if (!t.Result)
                {
                    tcs.TrySetResult(false);
                    return;
                }

                Accepted.Add(__instance, true);
                MethodInfo orig = AccessTools.Method(typeof(VideoTextureProvider), "LoadFromAsset");
                ValueTask vt = (ValueTask)orig.Invoke(__instance, new object[] { assetURL })!;

                vt.AsTask().ContinueWith(delegate { tcs.TrySetResult(true); });
            });

            return false;
        }

        [HarmonyPrefix, HarmonyPatch("LoadFromStreamURL")]
        public static bool StreamPrefix(VideoTextureProvider __instance, Uri streamURL, ref ValueTask __result)
        {
            if (!OptIn.Value) return true;

            if (Accepted.Remove(__instance))
                return true;

            TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
            __result = new ValueTask(tcs.Task);

            ShowPrompt(streamURL, CancellationToken.None).ContinueWith(t =>
            {
                if (!t.Result)
                {
                    tcs.TrySetResult(false);
                    return;
                }

                Accepted.Add(__instance, true);
                MethodInfo orig = AccessTools.Method(typeof(VideoTextureProvider), "LoadFromStreamURL");
                ValueTask vt = (ValueTask)orig.Invoke(__instance, new object[] { streamURL })!;

                vt.AsTask().ContinueWith(delegate { tcs.TrySetResult(true); });
            });

            return false;
        }

        private static Task<bool> ShowPrompt(Uri url, CancellationToken token)
        {
            Slot? slot = null;
            TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();

            try
            {
                Userspace.UserspaceWorld.RunSynchronously(delegate
                {
                    slot = Userspace.UserspaceWorld.AddSlot("Video Request");

                    UIBuilder uiBuilder = RadiantUI_Panel.SetupPanel(slot, "Video Request", new float2(400f, 300f), pinButton: false);
                    slot.LocalScale *= 0.0005f;

                    RadiantUI_Constants.SetupEditorStyle(uiBuilder);
                    uiBuilder.VerticalLayout(4f);
                    uiBuilder.Style.MinHeight = 64f;
                    uiBuilder.Text("Security.HostAccess.Warning".AsLocaleKey());
                    uiBuilder.Style.MinHeight = 32f;
                    Text host = uiBuilder.Text(url.ToString());
                    host.Color.Value = new colorX(0f, 1f, 1f);
                    uiBuilder.Style.MinHeight = 32f;
                    uiBuilder.HorizontalLayout(4f);
                    Button allow = uiBuilder.Button("Security.HostAccess.Allow".AsLocaleKey(), RadiantUI_Constants.Sub.GREEN);
                    allow.LocalPressed += (_, _) =>
                    {
                        tcs.TrySetResult(true);
                        Userspace.UserspaceWorld.RunSynchronously(() => slot?.Destroy());
                    };

                    Button deny = uiBuilder.Button("Security.HostAccess.Deny".AsLocaleKey(), RadiantUI_Constants.Sub.RED);
                    deny.LocalPressed += (_, _) =>
                    {
                        tcs.TrySetResult(false);
                        Userspace.UserspaceWorld.RunSynchronously(() => slot?.Destroy());
                    };

                    Button copy = uiBuilder.Button("Copy", RadiantUI_Constants.Sub.PURPLE);
                    copy.LocalPressed += (_, _) =>
                    {
                        if (url == null)
                            return;
                        Engine.Current.InputInterface.Clipboard?.SetText(url.ToString());
                    };

                    slot.PositionInFrontOfUser(float3.Backward);

                    Task.Delay(TimeSpan.FromSeconds(OptInTimeout.Value), token).ContinueWith(_ =>
                    {
                        tcs.TrySetResult(false);
                        Userspace.UserspaceWorld.RunSynchronously(() => slot?.Destroy());
                    }, token);
                }, true);
            }
            catch (Exception e)
            {
                Log.LogError($"Error in Whitelist: {e}");
                Userspace.UserspaceWorld.RunSynchronously(() => slot?.Destroy());
                tcs.TrySetResult(false);
            }

            return tcs.Task;
        }
    }
}