#version 450

layout (location = 0) in vec3 inPosition;
layout (location = 1) in vec3 inNormal;
layout (location = 2) in vec2 inTexCoord;
layout (location = 3) in vec4 inTangent;

layout (location = 0) out VS_OUT {
    vec3 position;
    vec2 texCoord;
    mat3 tbn;
} vs_out;

layout (push_constant) uniform PushConstants {
    mat4 mvp;
    mat4 model;
} pushConstants;

void main() {
    vs_out.position = (pushConstants.model * vec4(inPosition, 1.0)).xyz;
    vs_out.texCoord = inTexCoord;

    vec3 worldNormal = normalize((pushConstants.model * vec4(inNormal, 0.0)).xyz);
    vec3 worldTangent = normalize(pushConstants.model * vec4(inTangent.xyz, 0.0)).xyz;

    worldTangent = normalize(worldTangent - dot(worldTangent, worldNormal) * worldNormal);

    vec3 worldBitangent = cross(worldNormal, worldTangent) * inTangent.w;

    vs_out.tbn = mat3(worldTangent, worldBitangent, worldNormal);

    gl_Position = pushConstants.mvp * vec4(inPosition, 1.0);
}