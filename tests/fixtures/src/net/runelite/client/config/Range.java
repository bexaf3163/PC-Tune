package net.runelite.client.config;
import java.lang.annotation.*;
@Retention(RetentionPolicy.RUNTIME) @Target(ElementType.METHOD)
public @interface Range { int min() default 0; int max() default Integer.MAX_VALUE; }
