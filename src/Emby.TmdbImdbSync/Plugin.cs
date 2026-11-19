using System;
using System.Collections.Generic;
using System.IO;
using Emby.TmdbImdbSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.TmdbImdbSync
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public static readonly Guid PluginGuid = new Guid("987d6cf2-4db1-4e45-84fe-31da43cf8bf3");

        public override Guid Id => PluginGuid;
        public override string Name => "TMDb & IMDb Sync";
        public override string Description => "Synchronizes movie favorites and watched states between Emby, TMDb, and IMDb.";

        public static Plugin? Instance { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "TmdbImdbSyncConfig",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                    EnableInMainMenu = true,
                    MenuSection = "server",
                    MenuIcon = "sync",
                    DisplayName = "TMDb & IMDb Sync",
                    IsMainConfigPage = true
                },
                new PluginPageInfo
                {
                    Name = "TmdbImdbSyncConfigJs",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js"
                }
            };
        }

        public Stream GetThumbImage()
        {
            var type = GetType();
            var stream = type.Assembly.GetManifestResourceStream(type.Namespace + ".thumb.png");
            if (stream == null)
            {
                throw new FileNotFoundException("thumb.png resource not found");
            }
            return stream;
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;
    }
}
