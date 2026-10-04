using System.Numerics;
using System.Runtime.InteropServices;
using SharpGLTF.Schema2;
using StbImageSharp;
using VortexEngine.Rendering.Vulkan.Core;
using VortexEngine.Rendering.Vulkan.Shaders;
using Vortice.Vulkan;

namespace VortexEngine.Rendering.Vulkan.Resources;

internal sealed class MeshManager : IDisposable
{
    private readonly VkCommandPool commandPool;
    private readonly VulkanContext ctx;
    private readonly VkDescriptorPool descriptorPool;
    private readonly ShaderManager shaderManager;
    private readonly TextureManager textureManager;
    private List<Mesh> meshes = [];

    public MeshManager(VulkanContext context, VkCommandPool cmdPool, TextureManager textures, ShaderManager shaders,
        VkDescriptorPool descPool)
    {
        ctx = context;
        commandPool = cmdPool;
        textureManager = textures;
        shaderManager = shaders;
        descriptorPool = descPool;
    }

    public void Dispose()
    {
        foreach (var mesh in meshes)
        {
            foreach (var primitive in mesh.Primitives)
            {
                Vma.vmaDestroyBuffer(ctx.Allocator, primitive.VertexBuffer, primitive.VertexAllocation);
                Vma.vmaDestroyBuffer(ctx.Allocator, primitive.IndexBuffer, primitive.IndexAllocation);
            }
        }
    }

    public MeshHandle LoadMesh(byte[] meshData)
    {
        var model = ModelRoot.ParseGLB(meshData);
        var primitives = new List<Primitive>();

        foreach (var mesh in model.LogicalMeshes)
        {
            foreach (var primitive in mesh.Primitives)
            {
                var positions = primitive.GetVertexAccessor("POSITION").AsVector3Array().ToArray();
                var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array().ToArray() ??
                              ComputeNormals([.. positions], [.. primitive.GetIndices()]);

                var texCoords = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array().ToArray() ??
                                [.. Enumerable.Repeat(Vector2.Zero, positions.Length)];

                var indices = primitive.GetIndices().ToArray();

                var tangents = primitive.GetVertexAccessor("TANGENT")?.AsVector4Array().ToArray() ??
                               ComputeTangents(positions, normals, texCoords, indices);

                var vertices = new Vertex[positions.Length];
                for (var i = 0; i < positions.Length; i++)
                {
                    vertices[i] = new Vertex
                    {
                        Position = positions[i],
                        Normal = normals[i],
                        TexCoord = texCoords[i],
                        Tangent = tangents[i]
                    };
                }

                var vertexBuffer = CreateBuffer(
                    (ulong)(vertices.Length * sizeof(Vertex)),
                    VkBufferUsageFlags.VertexBuffer | VkBufferUsageFlags.TransferDst,
                    VmaMemoryUsage.AutoPreferDevice,
                    out var vertexAlloc
                );

                UploadMeshData(MemoryMarshal.AsBytes(vertices.AsSpan()), vertexBuffer, 0);

                var indexBuffer = CreateBuffer(
                    (ulong)(indices.Length * sizeof(uint)),
                    VkBufferUsageFlags.IndexBuffer | VkBufferUsageFlags.TransferDst,
                    VmaMemoryUsage.AutoPreferDevice,
                    out var indexAlloc);

                UploadMeshData(MemoryMarshal.AsBytes(indices.AsSpan()), indexBuffer, 0);

                var material = new Material
                {
                    Albedo = LoadMaterialTexture(primitive.Material, "BaseColor")
                             ?? textureManager.LoadDefaultTexture(Vector4.One),
                    Normal = LoadMaterialTexture(primitive.Material, "Normal")
                             ?? textureManager.LoadDefaultTexture(new Vector4(0.5f, 0.5f, 1, 1)),
                    MetallicRoughness = LoadMaterialTexture(primitive.Material, "MetallicRoughness")
                                        ?? textureManager.LoadDefaultTexture(new Vector4(0, 1, 0, 0)),
                    Occlusion = LoadMaterialTexture(primitive.Material, "Occlusion")
                                ?? textureManager.LoadDefaultTexture(Vector4.One),
                    Emissive = LoadMaterialTexture(primitive.Material, "Emissive")
                               ?? textureManager.LoadDefaultTexture(Vector4.Zero),
                };

                var prim = new Primitive
                {
                    VertexBuffer = vertexBuffer,
                    VertexAllocation = vertexAlloc,
                    IndexBuffer = indexBuffer,
                    IndexAllocation = indexAlloc,
                    IndexCount = (uint)indices.Length,
                    Material = material,
                    DescriptorSet = VkDescriptorSet.Null,
                };

                CreatePrimitiveDescriptorSet(ref prim, material);

                primitives.Add(prim);
            }
        }

        var meshObj = new Mesh { Primitives = primitives };
        meshes.Add(meshObj);

        return new MeshHandle((uint)(meshes.Count - 1));
    }

    private unsafe void CreatePrimitiveDescriptorSet(ref Primitive primitive, Material material)
    {
        var descriptorLayout = shaderManager.GetDescriptorSetLayout("pbr_mesh", 0);

        var allocInfo = new VkDescriptorSetAllocateInfo
        {
            sType = VkStructureType.DescriptorSetAllocateInfo,
            descriptorPool = descriptorPool,
            descriptorSetCount = 1,
            pSetLayouts = &descriptorLayout,
        };

        VkDescriptorSet descriptorSet;
        ctx.DeviceApi.vkAllocateDescriptorSets(&allocInfo, &descriptorSet).CheckResult();

        var imageInfos = stackalloc VkDescriptorImageInfo[7];

        imageInfos[0] = new VkDescriptorImageInfo
        {
            sampler = textureManager.GetTextureSampler(material.Albedo),
            imageView = textureManager.GetTextureImageView(material.Albedo),
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        imageInfos[1] = new VkDescriptorImageInfo
        {
            sampler = textureManager.GetTextureSampler(material.Normal),
            imageView = textureManager.GetTextureImageView(material.Normal),
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        imageInfos[2] = new VkDescriptorImageInfo
        {
            sampler = textureManager.GetTextureSampler(material.MetallicRoughness),
            imageView = textureManager.GetTextureImageView(material.MetallicRoughness),
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        imageInfos[3] = new VkDescriptorImageInfo
        {
            sampler = textureManager.GetTextureSampler(material.Occlusion),
            imageView = textureManager.GetTextureImageView(material.Occlusion),
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        imageInfos[4] = new VkDescriptorImageInfo
        {
            sampler = textureManager.GetTextureSampler(material.Emissive),
            imageView = textureManager.GetTextureImageView(material.Emissive),
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        imageInfos[5] = new VkDescriptorImageInfo
        {
            sampler = textureManager.CubemapSampler,
            imageView =
                textureManager.GetCubemapImageView(
                    textureManager.ActiveEnvironmentCubemap),
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        imageInfos[6] = new VkDescriptorImageInfo
        {
            sampler = textureManager.BrdfLutSampler,
            imageView = textureManager.BrdfLutImageView,
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        var writeDescriptorSets = stackalloc VkWriteDescriptorSet[7];

        for (var i = 0; i < 7; i++)
        {
            writeDescriptorSets[i] = new VkWriteDescriptorSet
            {
                sType = VkStructureType.WriteDescriptorSet,
                dstSet = descriptorSet,
                dstBinding = (uint)i,
                dstArrayElement = 0,
                descriptorCount = 1,
                descriptorType = VkDescriptorType.CombinedImageSampler,
                pImageInfo = &imageInfos[i],
            };
        }

        ctx.DeviceApi.vkUpdateDescriptorSets(7, writeDescriptorSets, 0, null);

        primitive.DescriptorSet = descriptorSet;
    }


    public Mesh GetMesh(MeshHandle handle) => meshes[(int)handle.Id];

    private unsafe VkBuffer CreateBuffer(ulong size, VkBufferUsageFlags usage, VmaMemoryUsage memoryUsage,
        out VmaAllocation allocation)
    {
        var bufferInfo = new VkBufferCreateInfo
        {
            sType = VkStructureType.BufferCreateInfo,
            size = size,
            usage = usage,
        };

        var allocInfo = new VmaAllocationCreateInfo
        {
            usage = memoryUsage,
            flags = VmaAllocationCreateFlags.HostAccessSequentialWrite
        };

        Vma.vmaCreateBuffer(ctx.Allocator, bufferInfo, allocInfo, out var buffer, out allocation, null)
            .CheckResult();

        return buffer;
    }

    private unsafe void CopyBuffer(VkBuffer srcBuffer, VkBuffer dstBuffer, ulong size)
    {
        var allocInfo = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.CommandBufferAllocateInfo,
            level = VkCommandBufferLevel.Primary,
            commandPool = commandPool,
            commandBufferCount = 1
        };

        ctx.DeviceApi.vkAllocateCommandBuffer(&allocInfo, out var copyCmd).CheckResult();

        var beginInfo = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.CommandBufferBeginInfo,
            flags = VkCommandBufferUsageFlags.OneTimeSubmit
        };

        ctx.DeviceApi.vkBeginCommandBuffer(copyCmd, &beginInfo).CheckResult();

        var copyRegion = new VkBufferCopy
        {
            srcOffset = 0,
            dstOffset = 0,
            size = size,
        };

        ctx.DeviceApi.vkCmdCopyBuffer(copyCmd, srcBuffer, dstBuffer, 1, &copyRegion);

        ctx.DeviceApi.vkEndCommandBuffer(copyCmd).CheckResult();

        var submitInfo = new VkSubmitInfo
        {
            sType = VkStructureType.SubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &copyCmd
        };

        ctx.DeviceApi.vkQueueSubmit(ctx.GraphicsQueue, submitInfo, VkFence.Null).CheckResult();
        ctx.DeviceApi.vkQueueWaitIdle(ctx.GraphicsQueue).CheckResult();

        ctx.DeviceApi.vkFreeCommandBuffers(commandPool, 1, &copyCmd);
    }

    private unsafe void UploadMeshData(ReadOnlySpan<byte> data, VkBuffer dstBuffer, ulong offset)
    {
        var stagingBuffer = CreateBuffer((ulong)data.Length,
            VkBufferUsageFlags.TransferSrc,
            VmaMemoryUsage.AutoPreferHost,
            out var stagingAlloc);

        void* mapped = null;
        var mapResult = Vma.vmaMapMemory(ctx.Allocator, stagingAlloc, &mapped);
        if (mapResult != VkResult.Success)
            throw new Exception($"Failed to map memory: {mapResult}");

        data.CopyTo(new Span<byte>(mapped, data.Length));
        Vma.vmaUnmapMemory(ctx.Allocator, stagingAlloc);

        CopyBuffer(stagingBuffer, dstBuffer, (ulong)data.Length);

        Vma.vmaDestroyBuffer(ctx.Allocator, stagingBuffer, stagingAlloc);
    }

    private Vector3[] ComputeNormals(Vector3[] positions, uint[] indices)
    {
        var normals = new Vector3[positions.Length];

        for (var i = 0; i < indices.Length; i += 3)
        {
            var i0 = (int)indices[i];
            var i1 = (int)indices[i + 1];
            var i2 = (int)indices[i + 2];

            var v0 = positions[i0];
            var v1 = positions[i1];
            var v2 = positions[i2];

            var edge1 = v1 - v0;
            var edge2 = v2 - v0;
            var faceNormal = Vector3.Cross(edge1, edge2);

            normals[i0] += faceNormal;
            normals[i1] += faceNormal;
            normals[i2] += faceNormal;
        }

        for (var i = 0; i < normals.Length; i++)
        {
            normals[i] = Vector3.Normalize(normals[i]);
        }

        return normals;
    }

    private Vector4[] ComputeTangents(Vector3[] positions, Vector3[] normals, Vector2[] texCoords, uint[] indices)
    {
        var tangents = new Vector3[positions.Length];
        var bitangents = new Vector3[positions.Length];
        for (var i = 0; i < indices.Length; i += 3)
        {
            var i0 = (int)indices[i];
            var i1 = (int)indices[i + 1];
            var i2 = (int)indices[i + 2];

            var v0 = positions[i0];
            var v1 = positions[i1];
            var v2 = positions[i2];

            var uv0 = texCoords[i0];
            var uv1 = texCoords[i1];
            var uv2 = texCoords[i2];

            var edge1 = v1 - v0;
            var edge2 = v2 - v0;

            var deltaUV1 = uv1 - uv0;
            var deltaUV2 = uv2 - uv0;

            var f = 1.0f / (deltaUV1.X * deltaUV2.Y - deltaUV2.X * deltaUV1.Y);

            var tangent = new Vector3(
                f * (deltaUV2.Y * edge1.X - deltaUV1.Y * edge2.X),
                f * (deltaUV2.Y * edge1.Y - deltaUV1.Y * edge2.Y),
                f * (deltaUV2.Y * edge1.Z - deltaUV1.Y * edge2.Z)
            );

            var bitangent = new Vector3(
                f * (-deltaUV2.X * edge1.X + deltaUV1.X * edge2.X),
                f * (-deltaUV2.X * edge1.Y + deltaUV1.X * edge2.Y),
                f * (-deltaUV2.X * edge1.Z + deltaUV1.X * edge2.Z)
            );

            tangents[i0] += tangent;
            tangents[i1] += tangent;
            tangents[i2] += tangent;

            bitangents[i0] += bitangent;
            bitangents[i1] += bitangent;
            bitangents[i2] += bitangent;
        }

        var result = new Vector4[positions.Length];

        for (var i = 0; i < positions.Length; i++)
        {
            var t = Vector3.Normalize(tangents[i]);
            var b = Vector3.Normalize(bitangents[i]);
            var n = normals[i];

            t = Vector3.Normalize(t - Vector3.Dot(t, n) * n);
            b = Vector3.Normalize(b - Vector3.Dot(b, n) * n);

            var handedness = Vector3.Dot(Vector3.Cross(n, t), b) < 0 ? -1.0f : 1.0f;

            result[i] = new Vector4(t.X, t.Y, t.Z, handedness);
        }

        return result;
    }

    private TextureHandle? LoadMaterialTexture(SharpGLTF.Schema2.Material material, string channelName)
    {
        if (material == null) return null;

        var textureInfo = material.FindChannel(channelName)?.Texture;
        if (textureInfo == null) return null;

        var image = textureInfo.PrimaryImage;
        if (image == null) return null;

        try
        {
            var imageData = image.Content.Content.ToArray();
            var img = ImageResult.FromMemory(imageData, ColorComponents.RedGreenBlueAlpha);

            var imageWidth = (uint)img.Width;
            var imageHeight = (uint)img.Height;

            return textureManager.LoadTexture(img.Data, imageWidth, imageHeight);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load texture for channel {channelName}: {ex.Message}");
            return null;
        }
    }
}
