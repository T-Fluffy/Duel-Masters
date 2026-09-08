shader_type canvas_item;

// Glass-shatter dissolve used by broken-shield ghosts: a mauve/gold tinted shard
// is eaten away by animated value noise with a glowing edge, then fades entirely.

uniform float u_progress : hint_range(0.0, 1.0) = 0.0;
uniform vec4 u_tint : source_color = vec4(1.0, 0.82, 0.35, 1.0);
uniform float u_edge_width = 0.14;

float hash(vec2 p)
{
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}

float noise2(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(
        mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x),
        mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x),
        u.y);
}

void fragment()
{
    vec2 uv = UV;
    vec4 tex = texture(TEXTURE, uv) * COLOR;
    vec3 base_col = tex.rgb * 0.35 + u_tint.rgb * 0.65;

    float n = noise2(uv * 18.0 + vec2(u_progress * 9.0));
    float m = noise2((uv + 1.7) * 26.0 - vec2(u_progress * 13.0));
    float shatter = n * 0.6 + m * 0.4;

    // Cutoff crawls across the shard; roughness makes it break instead of melt.
    float cutoff = u_progress + (shatter - 0.5) * 0.24;
    float eaten = smoothstep(0.5, 0.5 + u_edge_width, cutoff);
    float solid = 1.0 - smoothstep(0.32, 0.62, cutoff);

    vec3 col = mix(base_col, u_tint.rgb * 1.8, eaten * 0.9);
    float alpha = tex.a * solid * (1.0 - smoothstep(0.72, 1.0, u_progress * 1.25));
    COLOR = vec4(col, alpha);
    if (alpha < 0.01)
        discard;
}