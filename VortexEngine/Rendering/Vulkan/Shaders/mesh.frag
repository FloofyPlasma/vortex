#version 450

layout (set = 0, binding = 0) uniform sampler2D albedoTexture;
layout (set = 0, binding = 1) uniform sampler2D normalTexture;
layout (set = 0, binding = 2) uniform sampler2D metallicRoughnessTexture;
layout (set = 0, binding = 3) uniform sampler2D occlusionTexture;
layout (set = 0, binding = 4) uniform sampler2D emissiveTexture;
layout (set = 0, binding = 5) uniform samplerCube iblSpecularTexture;
layout (set = 0, binding = 6) uniform sampler2D brdfLUT;

layout (set = 1, binding = 0) uniform FrameConstants {
    vec4 cameraPos;
    vec4 directionalLight;  // dir.xyz, intensity
    vec4 directionalColor;
    vec4 ambientColor;      // color.rgb, intensity
    uint debugMode;         // 0=full PBR, 1=metallic, 2=roughness, 3=normal, 4=AO
    uint _pad1, _pad2, _pad3;
} frame;

layout (location = 0) in VS_OUT {
    vec3 position;
    vec2 texCoord;
    mat3 tbn;
} fs_in;

layout (location = 0) out vec4 outColor;

const float PI = 3.14159265359;

// Normal Distribution Function (Trowbridge-Reitz GGX)
float DistributionGGX(vec3 N, vec3 H, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH = max(dot(N, H), 0.0);
    float NdotH2 = NdotH * NdotH;
    float nom = a2;
    float denom = (NdotH2 * (a2 - 1.0) + 1.0);
    denom = PI * denom * denom;
    return nom / denom;
}

// Geometry Function (Schlick-GGX)
float GeometrySchlickGGX(float NdotV, float roughness)
{
    float r = (roughness + 1.0);
    float k = (r * r) / 8.0;
    float nom = NdotV;
    float denom = NdotV * (1.0 - k) + k;
    return nom / denom;
}

float GeometrySmith(vec3 N, vec3 V, vec3 L, float roughness)
{
    float NdotV = max(dot(N, V), 0.0);
    float NdotL = max(dot(N, L), 0.0);
    float ggx2 = GeometrySchlickGGX(NdotV, roughness);
    float ggx1 = GeometrySchlickGGX(NdotL, roughness);
    return ggx1 * ggx2;
}

// Fresnel-Schlick approximation
vec3 FresnelSchlick(float cosTheta, vec3 F0)
{
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

void main()
{
    vec3 albedo = texture(albedoTexture, fs_in.texCoord).rgb;

    vec3 normalMap = texture(normalTexture, fs_in.texCoord).rgb;
    normalMap = normalMap * 2.0 - 1.0;

    vec3 N = normalize(fs_in.tbn * normalMap);

    vec2 metallicRoughness = texture(metallicRoughnessTexture, fs_in.texCoord).bg;
    float metallic = metallicRoughness.x;
    float roughness = metallicRoughness.y;
    //    roughness = max(roughness, 0.001);  // Clamp to avoid specular artifacts

    float ao = texture(occlusionTexture, fs_in.texCoord).r;

    // Debug views
    if (frame.debugMode == 1u) {
        // View metallic (white = metal, black = dielectric)
        outColor = vec4(vec3(metallic), 1.0);
        return;
    } else if (frame.debugMode == 2u) {
        // View roughness (white = rough, black = smooth)
        outColor = vec4(vec3(roughness), 1.0);
        return;
    } else if (frame.debugMode == 3u) {
        // View normal map (blue = flat, colorful = bumpy)
        outColor = vec4(normalMap * 0.5 + 0.5, 1.0);
        return;
    } else if (frame.debugMode == 4u) {
        // View AO (white = lit, black = shadowed)
        outColor = vec4(vec3(ao), 1.0);
        return;
    }

    // Direct PBR Lighting
    // Per-fragment vectors
    vec3 V = normalize(frame.cameraPos.xyz - fs_in.position);
    vec3 L = normalize(-frame.directionalLight.xyz);  // Directional light
    vec3 H = normalize(V + L);

    // Base reflectivity
    vec3 F0 = mix(vec3(0.04), albedo, metallic);

    // Cook-Torrance BRDF
    float NDF = DistributionGGX(N, H, roughness);
    float G = GeometrySmith(N, V, L, roughness);
    vec3 F = FresnelSchlick(max(dot(H, V), 0.0), F0);

    vec3 kS = F;
    vec3 kD = (1.0 - kS) * (1.0 - metallic);

    float NdotL = max(dot(N, L), 0.0);
    float NdotV = max(dot(N, V), 0.0);
    vec3 specular = (NDF * G * F) / (4.0 * NdotV * NdotL + 0.001);

    vec3 Lo = (kD * albedo / PI + specular) * frame.directionalColor.rgb * frame.directionalLight.w * NdotL;

    // IBL Specular
    vec3 R = reflect(-V, N);
    vec3 iblSpecular = texture(iblSpecularTexture, R).rgb;

    // BDRF LUT lookup
    vec2 brdf = texture(brdfLUT, vec2(NdotV, roughness)).rg;

    vec3 specularIBL = iblSpecular * (F0 * brdf.x + brdf.y);

    // Ambient
    vec3 ambient = frame.ambientColor.rgb * frame.ambientColor.w * albedo * ao;

    vec3 emissive = texture(emissiveTexture, fs_in.texCoord).rgb;

    vec3 color = (Lo + specularIBL) * ao + ambient + emissive;

    // Tone mapping (Reinhard)
    color = color / (color + vec3(1.0));

    // Gamma correction
    color = pow(color, vec3(1.0 / 2.2));

    outColor = vec4(color, 1.0);
}