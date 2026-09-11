#version 450

layout (location = 0) in vec3 fragNormal;
layout (location = 1) in vec3 fragPos;

layout (location = 0) out vec4 outColor;

void main() {
    vec3 lightPos = normalize(vec3(0.0, 2.0, 3.0));
    vec3 viewPos = vec3(0.0, 2.0, 3.0);

    vec3 norm = normalize(fragNormal);
    vec3 lightDir = normalize(lightPos - fragPos);
    vec3 viewDir = normalize(viewPos - fragPos);
    vec3 reflectDir = reflect(-lightDir, norm);

    vec3 ambient = vec3(0.2);

    float diff = max(dot(norm, lightDir), 0.0);
    vec3 diffuse = diff * vec3(1.0);

    float spec = pow(max(dot(viewDir, reflectDir), 0.0), 32.0);
    vec3 specular = spec * vec3(1.0);

    vec3 result = ambient + diffuse + specular;
    outColor = vec4(result, 1.0);
}