shader_type canvas_item;

// Expanding radial ring used for attack impacts, summon pings and shield bursts.
// Drawn on a full-screen ColorRect; u_center is in UV space.

uniform float u_progress : hint_range(0.0, 1.0) = 0.0;
uniform vec2 u_center = vec2(0.5, 0.5);
uniform vec4 u_color : source_color = vec4(1.0, 0.82, 0.35, 1.0);
uniform float u_radius = 1.2;
uniform float u_thickness = 0.12;

void fragment()
{
    float dist = length(UV - u_center);
    float front = u_progress * u_radius;
    float ring = smoothstep(front - u_thickness, front, dist)
               * (1.0 - smoothstep(front, front + u_thickness * 1.6, dist));
    float fade = pow(1.0 - u_progress, 1.4);
    COLOR = vec4(u_color.rgb, ring * fade * u_color.a);
    if (COLOR.a < 0.01)
        discard;
}