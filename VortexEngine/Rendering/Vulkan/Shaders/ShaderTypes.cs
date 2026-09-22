using System.Numerics;
using Vortice.Vulkan;

namespace VortexEngine.Rendering.Vulkan.Shaders;

public class ShaderDefinition
{
    public string Name { get; set; }
    public string VertexShaderPath { get; set; }
    public string FragmentShaderPath { get; set; }

    public List<VertexInputDefinition> VertexInputs { get; set; } = [];
    public List<DescriptorSetDefinition> DescriptorSets { get; set; } = [];
    public PushConstantDefinition? PushConstants { get; set; }
}

public class ComputeShaderDefinition
{
    public string Name { get; set; }
    public string ComputeShaderPath { get; set; }

    public List<DescriptorSetDefinition> DescriptorSets { get; set; } = [];
    public PushConstantDefinition? PushConstants { get; set; }
}

public class VertexInputDefinition
{
    public uint Location { get; set; }
    public uint Binding { get; set; }
    public uint Offset { get; set; }
    public VkFormat Format { get; set; }
    public string Name { get; set; }
}

public class PushConstantDefinition
{
    public uint Offset { get; set; }
    public uint Size { get; set; }
    public VkShaderStageFlags Stages { get; set; }
}

public class DescriptorSetDefinition
{
    public uint Set { get; set; }
    public List<DescriptorBinding> Bindings { get; set; } = [];
}

public class DescriptorBinding
{
    public uint Binding { get; set; }
    public VkDescriptorType Type { get; set; }
    public VkShaderStageFlags Stages { get; set; }
    public string Name { get; set; }
}

// TODO: Probably use OffsetOf for the offsets...

public static class ShaderDefinitions
{
    public static ShaderDefinition PBRMesh = new()
    {
        Name = "pbr_mesh",

        VertexShaderPath =
            "VortexEngine/Rendering/Vulkan/Shaders/mesh.vert",

        FragmentShaderPath =
            "VortexEngine/Rendering/Vulkan/Shaders/mesh.frag",

        VertexInputs =
        [
            new()
            {
                Location = 0,
                Binding = 0,
                Offset = 0,
                Format = VkFormat.R32G32B32Sfloat,
                Name = "inPosition"
            },
            new()
            {
                Location = 1,
                Binding = 0,
                Offset = (uint)sizeof(Vector3),
                Format = VkFormat.R32G32B32Sfloat,
                Name = "inNormal"
            },
            new()
            {
                Location = 2,
                Binding = 0,
                Offset = (uint)(sizeof(Vector3) * 2),
                Format = VkFormat.R32G32Sfloat,
                Name = "inTexCoord"
            },
            new()
            {
                Location = 3,
                Binding = 0,
                Offset = (uint)(sizeof(Vector3) * 2 + sizeof(Vector2)),
                Format = VkFormat.R32G32B32A32Sfloat,
                Name = "inTangent"
            }
        ],

        DescriptorSets =
        [
            new()
            {
                Set = 0,
                Bindings =
                [
                    new()
                    {
                        Binding = 0,
                        Type = VkDescriptorType.CombinedImageSampler,
                        Stages = VkShaderStageFlags.Fragment,
                        Name = "albedoTexture"
                    },
                    new()
                    {
                        Binding = 1,
                        Type = VkDescriptorType.CombinedImageSampler,
                        Stages = VkShaderStageFlags.Fragment,
                        Name = "normalTexture"
                    },
                    new()
                    {
                        Binding = 2,
                        Type = VkDescriptorType.CombinedImageSampler,
                        Stages = VkShaderStageFlags.Fragment,
                        Name = "metallicRoughnessTexture"
                    },
                    new()
                    {
                        Binding = 3,
                        Type = VkDescriptorType.CombinedImageSampler,
                        Stages = VkShaderStageFlags.Fragment,
                        Name = "occlusionTexture"
                    },
                    new()
                    {
                        Binding = 4,
                        Type = VkDescriptorType.CombinedImageSampler,
                        Stages = VkShaderStageFlags.Fragment,
                        Name = "emissiveTexture"
                    },
                    new()
                    {
                        Binding = 5,
                        Type = VkDescriptorType.CombinedImageSampler,
                        Stages = VkShaderStageFlags.Fragment,
                        Name = "iblSpecularTexture"
                    },
                    new()
                    {
                        Binding = 6,
                        Type = VkDescriptorType.CombinedImageSampler,
                        Stages = VkShaderStageFlags.Fragment,
                        Name = "brdfLUT"
                    }
                ]
            },
            new()
            {
                Set = 1,
                Bindings =
                [
                    new()
                    {
                        Binding = 0,
                        Type = VkDescriptorType.UniformBuffer,
                        Stages = VkShaderStageFlags.Fragment,
                        Name = "frame"
                    }
                ]
            }
        ],

        PushConstants = new()
        {
            Stages = VkShaderStageFlags.Vertex,
            Offset = 0,
            Size = (uint)(sizeof(Matrix4x4) * 2)
        }
    };

    public static ComputeShaderDefinition EquirectangularToCubemap = new()
    {
        Name = "equirectangular_to_cubemap",
        ComputeShaderPath =
            "VortexEngine/Rendering/Vulkan/Shaders/equirectangular_to_cubemap.comp",

        DescriptorSets =
        [
            new()
            {
                Set = 0,
                Bindings =
                [
                    new()
                    {
                        Binding = 0,
                        Type = VkDescriptorType.CombinedImageSampler,
                        Stages = VkShaderStageFlags.Compute,
                        Name = "equirectangularTexture"
                    },
                    new()
                    {
                        Binding = 1,
                        Type = VkDescriptorType.StorageImage,
                        Stages = VkShaderStageFlags.Compute,
                        Name = "cubemapFace"
                    }
                ]
            }
        ],

        PushConstants = new()
        {
            Stages = VkShaderStageFlags.Compute,
            Offset = 0,
            Size = sizeof(int) * 4
        }
    };

    public static ComputeShaderDefinition BrdfLut = new()
    {
        Name = "brdf_lut",
        ComputeShaderPath =
            "VortexEngine/Rendering/Vulkan/Shaders/brdf_lut.comp",

        DescriptorSets =
        [
            new()
            {
                Set = 0,
                Bindings =
                [
                    new()
                    {
                        Binding = 0,
                        Type = VkDescriptorType.StorageImage,
                        Stages = VkShaderStageFlags.Compute,
                        Name = "brdfLUT"
                    }
                ]
            }
        ]
    };
}