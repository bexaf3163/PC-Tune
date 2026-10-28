package rs117.hd;
import net.runelite.client.config.*;
import rs117.hd.config.DefaultBoolean;
// Mirrors the 117 HD shape that broke presets: tiledLighting is an enum, not a boolean.
@ConfigGroup("hd")
public interface HdPluginConfig
{
	@ConfigItem(keyName = "tiledLighting", name = "Tiled lighting")
	default DefaultBoolean tiledLighting() { return DefaultBoolean.DEFAULT; }

	@ConfigItem(keyName = "unlockFps", name = "Unlock FPS")
	default boolean unlockFps() { return false; }

	@Range(min = 0, max = 999)
	@ConfigItem(keyName = "fpsTarget", name = "FPS target")
	default int fpsTarget() { return 60; }

	@Range(max = 184)
	@ConfigItem(keyName = "drawDistance", name = "Draw distance")
	default int drawDistance() { return 50; }
}
