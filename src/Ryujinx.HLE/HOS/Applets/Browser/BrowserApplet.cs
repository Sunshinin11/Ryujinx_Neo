using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.HLE.HOS.Services.Am.AppletAE;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Ryujinx.HLE.HOS.Applets.Browser
{
    internal class BrowserApplet : IApplet
    {
        public event EventHandler AppletStateChanged;

        private AppletSession _normalSession;

        private CommonArguments _commonArguments;
        private List<BrowserArgument> _arguments;
        private ShimKind _shimKind;

        public ResultCode Start(AppletSession normalSession, AppletSession interactiveSession)
        {
            _normalSession = normalSession;

            _commonArguments = IApplet.ReadStruct<CommonArguments>(_normalSession.Pop());

            Logger.Stub?.PrintStub(LogClass.ServiceAm, $"WebApplet version: 0x{_commonArguments.AppletVersion:x8}");

            ReadOnlySpan<byte> webArguments = _normalSession.Pop();

            (_shimKind, _arguments) = BrowserArgument.ParseArguments(webArguments);

            Logger.Stub?.PrintStub(LogClass.ServiceAm, $"Web Arguments: {_arguments.Count}");

            foreach (BrowserArgument argument in _arguments)
            {
                Logger.Stub?.PrintStub(LogClass.ServiceAm, $"{argument.Type}: {argument.GetValue()}");
            }

            // NEOV2-WEBOFF1: per-game option "Termos offline". The offline browser ends when its page navigates to
            // http://localhost/..., and that address is the result the game reads (e.g. the "accept" button of the
            // BioShock legal terms). Without it the game keeps reopening the applet. Find that address in the game's
            // own page; with the option off (or no address found) the previous behavior is kept untouched.
            bool offlineTerms = Ryujinx.Common.Diagnostics.NeoGraphicsSettings.WebOfflineTermsEnabled && _shimKind == ShimKind.Offline;
            string lastUrl = null;

            if (offlineTerms)
            {
                string documentPath = _arguments.Find(a => a.Type == WebArgTLVType.DocumentPath)?.GetValue() as string;

                NeoOfflineHtml.TryFindLastUrl(documentPath, out lastUrl);
            }

            // Web and Offline browser applets use LastUrl as the completion result.
            // Non-navigation shims (Shop, Login, Share, etc.) do not depend on navigation state and expect ExitButton.
            WebExitReason exitReason = (_shimKind == ShimKind.Web || _shimKind == ShimKind.Offline)
                ? WebExitReason.LastUrl
                : WebExitReason.ExitButton;

            // NEOV2-WEBOFF3: offline pages with the JS extension (BioShock legal terms) have no localhost navigation;
            // their confirm button calls window.close(), which ends the applet with WindowClosed and LastUrl "http://localhost/".
            if (offlineTerms && lastUrl == null && _arguments.Exists(a => a.Type == WebArgTLVType.JsExtensionEnabled && a.Value.Length > 0 && a.Value[0] != 0))
            {
                exitReason = WebExitReason.WindowClosed;
                lastUrl = "http://localhost/";
            }

            if ((_commonArguments.AppletVersion >= 0x80000 && _shimKind == ShimKind.Web) || (_commonArguments.AppletVersion >= 0x30000 && _shimKind == ShimKind.Share))
            {
                List<BrowserOutput> result =
                [
                    new(BrowserOutputType.ExitReason, (uint)exitReason)
                ];

                _normalSession.Push(BuildResponseNew(result));
            }
            else
            {
                WebCommonReturnValue result = new()
                {
                    ExitReason = exitReason,
                };

                if (lastUrl != null)
                {
                    byte[] urlBytes = Encoding.UTF8.GetBytes(lastUrl);
                    int urlLength = Math.Min(urlBytes.Length, result.LastUrl.Length - 1);

                    urlBytes.AsSpan(0, urlLength).CopyTo(result.LastUrl.AsSpan());
                    result.LastUrlSize = (ulong)urlLength;
                }

                _normalSession.Push(BuildResponseOld(result));
            }

            AppletStateChanged?.Invoke(this, null);

            return ResultCode.Success;
        }

        private static byte[] BuildResponseOld(WebCommonReturnValue result)
        {
            using MemoryStream stream = MemoryStreamManager.Shared.GetStream();
            using BinaryWriter writer = new(stream);
            writer.WriteStruct(result);

            return stream.ToArray();
        }
        private byte[] BuildResponseNew(List<BrowserOutput> outputArguments)
        {
            using MemoryStream stream = MemoryStreamManager.Shared.GetStream();
            using BinaryWriter writer = new(stream);
            writer.WriteStruct(new WebArgHeader
            {
                Count = (ushort)outputArguments.Count,
                ShimKind = _shimKind,
            });

            foreach (BrowserOutput output in outputArguments)
            {
                output.Write(writer);
            }

            writer.Write(new byte[0x2000 - writer.BaseStream.Position]);

            return stream.ToArray();
        }
    }
}
