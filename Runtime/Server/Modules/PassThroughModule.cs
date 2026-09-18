using System.Threading.Tasks;
using EmbedIO;
using Nox.CCK.Utils;

namespace Nox.Control.Runtime.Server.Modules {
	internal sealed class PassThroughModule  : WebModuleBase {
		public PassThroughModule() : base("/") { }

		/// <inheritdoc />
		public override bool IsFinalHandler => false;

		/// <inheritdoc />
        protected override async Task OnRequestAsync(IHttpContext context) {
            var request = context.Request;

            var method    = request.HttpMethod?.ToUpperInvariant();
            var path      = ControlServer.RelativePath(context, BaseRoute);
            var query     = request.Url?.Query;
            var ip  = request.RemoteEndPoint?.Address?.ToString() ?? "unknown";

            Logger.Log(
                $"[{ip}] {method} /{path}{query}",
                tag: nameof(ControlServer)
            );
        }
	}
}
