package net.runelite.client.config;
import java.lang.annotation.*;
@Retention(RetentionPolicy.RUNTIME) @Target(ElementType.METHOD)
public @interface ConfigItem { String keyName(); String name() default ""; int position() default -1; }
