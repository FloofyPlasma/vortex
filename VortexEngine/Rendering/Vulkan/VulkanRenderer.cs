using System.Numerics;
using System.Runtime.InteropServices;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp.PixelFormats;
using VortexEngine.Rendering.Vulkan.Core;
using VortexEngine.Rendering.Vulkan.Shaders;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;
using Image = SixLabors.ImageSharp.Image;

namespace VortexEngine.Rendering.Vulkan;

// Unsafe is unavoidable due to Vulkan API design lol
internal sealed unsafe class VulkanRenderer : IDisposable
{
    private const int MaxFramesInFlight = 2;
    private VkImage brdfLUTImage;
    private VkImageView brdfLUTImageView;
    private VkSampler brdfLUTSampler;

    private VkShaderModule brdfLutShader;
    private VkCommandBuffer[] commandBuffers = null!;

    private VkCommandPool commandPool;
    private VulkanContext context;
    private VkSampler cubemapSampler;

    private List<CubemapData> cubemapTextures = [];
    private int currentFrame = 0;

    private VkDescriptorPool descriptorPool;

    private VmaAllocation frameConstantAllocation;

    private VkBuffer frameConstantBuffer;
    private VkDescriptorSet frameDescriptorSet;

    private uint imageCount;

    private List<Mesh> meshes = [];

    // Debug Mesh Stuff
    private float rotation;

    private ShaderManager shaderManager;

    private SwapchainManager swapchain;

    private SyncManager sync;
    private List<VkImageView> textureImageViews = [];
    private List<VkImage> textureImages = [];
    private List<VkSampler> textureSamplers = [];

    public VulkanRenderer(IVulkanSurfaceProvider surfaceProvider, uint width, uint height)
    {
        context = new VulkanContext(surfaceProvider);
        swapchain = new SwapchainManager(context, width, height);
        sync = new SyncManager(context);
        shaderManager = new ShaderManager(context, swapchain);

        CreateCubemapSamplers();
        CreateFrameConstantBuffer();
        CreateDescriptorPool();
        CreateFrameDescriptorSet();
        CreateCommandPool();
        TransitionDepthImage();
        CreateCommandBuffers();
        GenerateBRDFLUT();
    }

    public void Dispose()
    {
        foreach (var mesh in meshes)
        {
            foreach (var primitive in mesh.Primitives)
            {
                Vma.vmaDestroyBuffer(context.Allocator, primitive.VertexBuffer, primitive.VertexAllocation);
                Vma.vmaDestroyBuffer(context.Allocator, primitive.IndexBuffer, primitive.IndexAllocation);
            }
        }

        foreach (var cubemap in cubemapTextures)
        {
            for (var i = 0; i < 6; i++)
            {
                context.DeviceApi.vkDestroyImageView(cubemap.Views[i], null);
                Vma.vmaDestroyImage(context.Allocator, cubemap.Images[i], VmaAllocation.Null);
            }
        }

        context.DeviceApi.vkDestroySampler(cubemapSampler, null);
        context.DeviceApi.vkDestroySampler(brdfLUTSampler, null);

        if (brdfLUTImageView.Handle != 0)
        {
            context.DeviceApi.vkDestroyImageView(brdfLUTImageView, null);
        }

        if (brdfLUTImage.Handle != 0)
        {
            Vma.vmaDestroyImage(context.Allocator, brdfLUTImage, VmaAllocation.Null);
        }

        Vma.vmaDestroyBuffer(context.Allocator, frameConstantBuffer, frameConstantAllocation);

        shaderManager.Dispose();
        sync.Dispose();
        swapchain.Dispose();
        context.Dispose();
    }

    private void CreateCommandPool()
    {
        var poolInfo = new VkCommandPoolCreateInfo
        {
            sType = VkStructureType.CommandPoolCreateInfo,
            flags = VkCommandPoolCreateFlags.ResetCommandBuffer,
            queueFamilyIndex = context.GraphicsQueueFamily,
        };

        context.DeviceApi.vkCreateCommandPool(&poolInfo, null, out commandPool).CheckResult();
    }

    private unsafe void CreateCommandBuffers()
    {
        commandBuffers = new VkCommandBuffer[swapchain.Images.Length];
        var allocInfo = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.CommandBufferAllocateInfo,
            commandPool = commandPool,
            level = VkCommandBufferLevel.Primary,
            commandBufferCount = (uint)commandBuffers.Length
        };

        fixed (VkCommandBuffer* pCommandBuffers = commandBuffers)
        {
            context.DeviceApi.vkAllocateCommandBuffers(&allocInfo, pCommandBuffers).CheckResult();
        }
    }

    public void Render()
    {
        sync.WaitForFrame(currentFrame);

        var imageAvail = sync.GetImageAvailableSemaphore(currentFrame);
        var renderDone = sync.GetRenderFinishedSemaphore(currentFrame);
        var fence = sync.GetInFlightFence(currentFrame);

        var imageIndex = swapchain.AcquireNextImage(imageAvail);

        sync.ResetFrameFence(currentFrame);

        context.DeviceApi.vkResetCommandBuffer(commandBuffers[imageIndex], VkCommandBufferResetFlags.None)
            .CheckResult();

        var beginInfo = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.CommandBufferBeginInfo,
        };

        context.DeviceApi.vkBeginCommandBuffer(commandBuffers[imageIndex], &beginInfo).CheckResult();

        var colorAttachment = new VkRenderingAttachmentInfo
        {
            sType = VkStructureType.RenderingAttachmentInfo,
            imageView = swapchain.ImageViews[imageIndex],
            imageLayout = VkImageLayout.ColorAttachmentOptimal,
            clearValue = new VkClearValue { color = new VkClearColorValue(0.0f, 0.0f, 0.0f, 1.0f) },
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.Store
        };

        var depthAttachment = new VkRenderingAttachmentInfo
        {
            sType = VkStructureType.RenderingAttachmentInfo,
            imageView = swapchain.DepthImageView,
            imageLayout = VkImageLayout.DepthStencilAttachmentOptimal,
            clearValue = new VkClearValue { depthStencil = new VkClearDepthStencilValue(depth: 1.0f, stencil: 0) },
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.DontCare,
        };

        var renderingInfo = new VkRenderingInfo
        {
            sType = VkStructureType.RenderingInfo,
            renderArea = new VkRect2D { offset = new VkOffset2D(0, 0), extent = swapchain.Extent },
            layerCount = 1,
            colorAttachmentCount = 1,
            pColorAttachments = &colorAttachment,
            pDepthAttachment = &depthAttachment,
        };

        context.DeviceApi.vkCmdBeginRendering(commandBuffers[imageIndex], &renderingInfo);

        if (meshes.Count > 0)
        {
            {
                // var model = Matrix4x4.CreateRotationY(rotation) * Matrix4x4.CreateRotationX(rotation) *
                // Matrix4x4.CreateScale(1.0f);

                var model = Matrix4x4.CreateScale(0.5f) * Matrix4x4.CreateRotationX(MathF.PI);

                var view = Matrix4x4.CreateLookAt(
                    new Vector3(0, 15, 10),
                    Vector3.Zero,
                    Vector3.UnitY
                );

                var projection = Matrix4x4.CreatePerspectiveFieldOfView(
                    MathF.PI / 4.0f,
                    swapchain.Extent.width / (float)swapchain.Extent.height,
                    0.1f,
                    100.0f
                );

                var mvp = model * view * projection;

                var mesh = meshes[0];
                var offset = 0UL;
                context.DeviceApi.vkCmdBindPipeline(commandBuffers[imageIndex], VkPipelineBindPoint.Graphics,
                    shaderManager.GetGraphicsPipeline("pbr_mesh"));

                var pushData = new PushConstants { Mvp = mvp, Model = model };
                context.DeviceApi.vkCmdPushConstants(commandBuffers[imageIndex],
                    shaderManager.GetPipelineLayout("pbr_mesh"),
                    VkShaderStageFlags.Vertex, 0,
                    (uint)sizeof(PushConstants), &pushData);

                var frameConstants = new FrameConstants
                {
                    CameraPos = new Vector4(0, 15, 10, 0),
                    DirectionalLight = new Vector4(0, -2.5f, -3.5f, 2),
                    DirectionalColor = new Vector4(0.8f, 0.8f, 0.8f, 0),
                    AmbientColor = new Vector4(0.3f, 0.3f, 0.3f, 0.3f),
                    DebugMode = 0, // 0 = full PBR, 1 = metallic, 2 = roughness, 3 = normal, 4 = AO
                };

                UploadFrameConstants(frameConstants);

                fixed (VkDescriptorSet* pFrameDescriptorSet = &frameDescriptorSet)
                {
                    context.DeviceApi.vkCmdBindDescriptorSets(commandBuffers[imageIndex], VkPipelineBindPoint.Graphics,
                        shaderManager.GetPipelineLayout("pbr_mesh"), 1, 1, pFrameDescriptorSet, 0, null);
                }

                foreach (var primitive in mesh.Primitives)
                {
                    context.DeviceApi.vkCmdBindDescriptorSets(commandBuffers[imageIndex], VkPipelineBindPoint.Graphics,
                        shaderManager.GetPipelineLayout("pbr_mesh"), 0, 1, &primitive.DescriptorSet, 0, null);

                    context.DeviceApi.vkCmdBindVertexBuffer(commandBuffers[imageIndex], 0, primitive.VertexBuffer,
                        offset);
                    context.DeviceApi.vkCmdBindIndexBuffer(commandBuffers[imageIndex], primitive.IndexBuffer, 0,
                        VkIndexType.Uint32);
                    context.DeviceApi.vkCmdDrawIndexed(commandBuffers[imageIndex], primitive.IndexCount, 1, 0, 0, 0);
                }
            }
        }

        context.DeviceApi.vkCmdEndRendering(commandBuffers[imageIndex]);

        context.DeviceApi.vkEndCommandBuffer(commandBuffers[imageIndex]);

        var waitSemaphores = new[] { imageAvail };
        var signalSemaphores = new[] { renderDone };
        var waitStages = new[] { VkPipelineStageFlags.ColorAttachmentOutput };

        fixed (VkSemaphore* pWaitSemaphores = waitSemaphores)
        fixed (VkPipelineStageFlags* pWaitStages = waitStages)
        fixed (VkCommandBuffer* pCommandBuffers = &commandBuffers[imageIndex])
        fixed (VkSemaphore* pSignalSemaphores = signalSemaphores)
        {
            var submitInfo = new VkSubmitInfo
            {
                sType = VkStructureType.SubmitInfo,
                waitSemaphoreCount = 1,
                pWaitSemaphores = pWaitSemaphores,
                pWaitDstStageMask = pWaitStages,
                commandBufferCount = 1,
                pCommandBuffers = pCommandBuffers,
                signalSemaphoreCount = 1,
                pSignalSemaphores = pSignalSemaphores
            };

            context.DeviceApi.vkQueueSubmit(context.GraphicsQueue, submitInfo, fence).CheckResult();
        }

        swapchain.Present(renderDone, imageIndex);

        currentFrame = (currentFrame + 1) % MaxFramesInFlight;
        rotation = (rotation + 0.01f) % (2.0f * MathF.PI);
    }

    private VkBuffer CreateBuffer(ulong size, VkBufferUsageFlags usage, VmaMemoryUsage memoryUsage,
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

        Vma.vmaCreateBuffer(context.Allocator, bufferInfo, allocInfo, out var buffer, out allocation, null)
            .CheckResult();

        return buffer;
    }

    private void CopyBuffer(VkBuffer srcBuffer, VkBuffer dstBuffer, ulong size)
    {
        var allocInfo = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.CommandBufferAllocateInfo,
            level = VkCommandBufferLevel.Primary,
            commandPool = commandPool,
            commandBufferCount = 1
        };

        context.DeviceApi.vkAllocateCommandBuffer(&allocInfo, out var copyCmd).CheckResult();

        var beginInfo = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.CommandBufferBeginInfo,
            flags = VkCommandBufferUsageFlags.OneTimeSubmit
        };

        context.DeviceApi.vkBeginCommandBuffer(copyCmd, &beginInfo).CheckResult();

        var copyRegion = new VkBufferCopy
        {
            srcOffset = 0,
            dstOffset = 0,
            size = size,
        };

        context.DeviceApi.vkCmdCopyBuffer(copyCmd, srcBuffer, dstBuffer, 1, &copyRegion);

        context.DeviceApi.vkEndCommandBuffer(copyCmd).CheckResult();

        var submitInfo = new VkSubmitInfo
        {
            sType = VkStructureType.SubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &copyCmd
        };

        context.DeviceApi.vkQueueSubmit(context.GraphicsQueue, submitInfo, VkFence.Null).CheckResult();
        context.DeviceApi.vkQueueWaitIdle(context.GraphicsQueue).CheckResult();

        context.DeviceApi.vkFreeCommandBuffers(commandPool, 1, &copyCmd);
    }

    private void UploadMeshData(ReadOnlySpan<byte> data, VkBuffer dstBuffer, ulong offset)
    {
        var stagingBuffer = CreateBuffer((ulong)data.Length,
            VkBufferUsageFlags.TransferSrc,
            VmaMemoryUsage.AutoPreferHost,
            out var stagingAlloc);

        void* mapped = null;
        var mapResult = Vma.vmaMapMemory(context.Allocator, stagingAlloc, &mapped);
        if (mapResult != VkResult.Success)
            throw new Exception($"Failed to map memory: {mapResult}");

        data.CopyTo(new Span<byte>(mapped, data.Length));
        Vma.vmaUnmapMemory(context.Allocator, stagingAlloc);

        CopyBuffer(stagingBuffer, dstBuffer, (ulong)data.Length);

        Vma.vmaDestroyBuffer(context.Allocator, stagingBuffer, stagingAlloc);
    }

    private void UploadMeshData(ReadOnlySpan<byte> data, VkImage dstImage, uint width, uint height, VkFormat format)
    {
        var stagingBuffer = CreateBuffer((ulong)data.Length, VkBufferUsageFlags.TransferSrc,
            VmaMemoryUsage.AutoPreferHost, out var stagingAlloc);

        void* mapped = null;
        Vma.vmaMapMemory(context.Allocator, stagingAlloc, &mapped).CheckResult();
        data.CopyTo(new Span<byte>(mapped, data.Length));
        Vma.vmaUnmapMemory(context.Allocator, stagingAlloc);

        TransitionImageLayout(dstImage, format, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal);

        CopyBufferToImage(stagingBuffer, dstImage, width, height);

        TransitionImageLayout(dstImage, format, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal);

        Vma.vmaDestroyBuffer(context.Allocator, stagingBuffer, stagingAlloc);
    }

    private void TransitionImageLayout(VkImage image, VkFormat format, VkImageLayout oldLayout, VkImageLayout newLayout)
    {
        var allocInfo = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.CommandBufferAllocateInfo,
            level = VkCommandBufferLevel.Primary,
            commandPool = commandPool,
            commandBufferCount = 1,
        };

        context.DeviceApi.vkAllocateCommandBuffer(&allocInfo, out var cmd).CheckResult();

        var beginInfo = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.CommandBufferBeginInfo,
            flags = VkCommandBufferUsageFlags.OneTimeSubmit
        };

        context.DeviceApi.vkBeginCommandBuffer(cmd, &beginInfo).CheckResult();

        var barrier = new VkImageMemoryBarrier
        {
            sType = VkStructureType.ImageMemoryBarrier,
            oldLayout = oldLayout,
            newLayout = newLayout,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = image,
            subresourceRange = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.Color,
                baseMipLevel = 0,
                levelCount = 1,
                baseArrayLayer = 0,
                layerCount = 1
            }
        };

        var srcStage = VkPipelineStageFlags.TopOfPipe;
        var dstStage = VkPipelineStageFlags.FragmentShader;

        if (oldLayout == VkImageLayout.Undefined && newLayout == VkImageLayout.TransferDstOptimal)
        {
            barrier.srcAccessMask = 0;
            barrier.dstAccessMask = VkAccessFlags.TransferWrite;
        }
        else if (oldLayout == VkImageLayout.TransferDstOptimal && newLayout == VkImageLayout.ShaderReadOnlyOptimal)
        {
            barrier.srcAccessMask = VkAccessFlags.TransferWrite;
            barrier.dstAccessMask = VkAccessFlags.ShaderRead;
            dstStage = VkPipelineStageFlags.FragmentShader;
        }

        context.DeviceApi.vkCmdPipelineBarrier(cmd, srcStage, dstStage, VkDependencyFlags.None, 0, null, 0, null, 1,
            &barrier);

        context.DeviceApi.vkEndCommandBuffer(cmd).CheckResult();

        var submitInfo = new VkSubmitInfo
        {
            sType = VkStructureType.SubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &cmd
        };

        context.DeviceApi.vkQueueSubmit(context.GraphicsQueue, submitInfo, VkFence.Null).CheckResult();
        context.DeviceApi.vkQueueWaitIdle(context.GraphicsQueue).CheckResult();

        context.DeviceApi.vkFreeCommandBuffers(commandPool, 1, &cmd);
    }

    private void CopyBufferToImage(VkBuffer buffer, VkImage image, uint width, uint height)
    {
        var allocInfo = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.CommandBufferAllocateInfo,
            level = VkCommandBufferLevel.Primary,
            commandPool = commandPool,
            commandBufferCount = 1
        };

        context.DeviceApi.vkAllocateCommandBuffer(&allocInfo, out var cmd).CheckResult();

        var beginInfo = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.CommandBufferBeginInfo,
            flags = VkCommandBufferUsageFlags.OneTimeSubmit
        };

        context.DeviceApi.vkBeginCommandBuffer(cmd, &beginInfo).CheckResult();

        var region = new VkBufferImageCopy
        {
            bufferOffset = 0,
            bufferRowLength = 0,
            bufferImageHeight = 0,
            imageSubresource = new VkImageSubresourceLayers
            {
                aspectMask = VkImageAspectFlags.Color,
                mipLevel = 0,
                baseArrayLayer = 0,
                layerCount = 1,
            },
            imageOffset = new VkOffset3D { x = 0, y = 0, z = 0 },
            imageExtent = new VkExtent3D { width = width, height = height, depth = 1 },
        };

        context.DeviceApi.vkCmdCopyBufferToImage(cmd, buffer, image, VkImageLayout.TransferDstOptimal, 1, &region);

        context.DeviceApi.vkEndCommandBuffer(cmd).CheckResult();

        var submitInfo = new VkSubmitInfo
        {
            sType = VkStructureType.SubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &cmd
        };

        context.DeviceApi.vkQueueSubmit(context.GraphicsQueue, submitInfo, VkFence.Null).CheckResult();
        context.DeviceApi.vkQueueWaitIdle(context.GraphicsQueue).CheckResult();

        context.DeviceApi.vkFreeCommandBuffers(commandPool, 1, &cmd);
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

    private void CreateDescriptorPool()
    {
        var poolSize = new VkDescriptorPoolSize
        {
            type = VkDescriptorType.CombinedImageSampler,
            descriptorCount = 256,
        };

        var createInfo = new VkDescriptorPoolCreateInfo
        {
            sType = VkStructureType.DescriptorPoolCreateInfo,
            poolSizeCount = 1,
            pPoolSizes = &poolSize,
            maxSets = 256,
        };

        context.DeviceApi.vkCreateDescriptorPool(&createInfo, null, out descriptorPool).CheckResult();
    }

    private void CreatePrimitiveDescriptorSet(ref Primitive primitive, Material material)
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
        context.DeviceApi.vkAllocateDescriptorSets(&allocInfo, &descriptorSet).CheckResult();

        var imageInfos = stackalloc VkDescriptorImageInfo[7];

        imageInfos[0] = new VkDescriptorImageInfo
        {
            sampler = textureSamplers[(int)material.Albedo.Id],
            imageView = textureImageViews[(int)material.Albedo.Id],
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        imageInfos[1] = new VkDescriptorImageInfo
        {
            sampler = textureSamplers[(int)material.Normal.Id],
            imageView = textureImageViews[(int)material.Normal.Id],
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        imageInfos[2] = new VkDescriptorImageInfo
        {
            sampler = textureSamplers[(int)material.MetallicRoughness.Id],
            imageView = textureImageViews[(int)material.MetallicRoughness.Id],
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        imageInfos[3] = new VkDescriptorImageInfo
        {
            sampler = textureSamplers[(int)material.Occlusion.Id],
            imageView = textureImageViews[(int)material.Occlusion.Id],
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        imageInfos[4] = new VkDescriptorImageInfo
        {
            sampler = textureSamplers[(int)material.Emissive.Id],
            imageView = textureImageViews[(int)material.Emissive.Id],
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        imageInfos[5] = new VkDescriptorImageInfo
        {
            sampler = cubemapSampler,
            imageView = cubemapTextures[0].Views[0],
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        imageInfos[6] = new VkDescriptorImageInfo
        {
            sampler = brdfLUTSampler,
            imageView = brdfLUTImageView,
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

        context.DeviceApi.vkUpdateDescriptorSets(7, writeDescriptorSets, 0, null);

        primitive.DescriptorSet = descriptorSet;
    }

    private TextureHandle LoadDefaultTexture(Vector4 color)
    {
        var r = (byte)(color.X * 255);
        var g = (byte)(color.Y * 255);
        var b = (byte)(color.Z * 255);
        var a = (byte)(color.W * 255);

        byte[] pixelData = [r, g, b, a];

        return LoadTexture(pixelData, 1, 1, VkFormat.R8G8B8A8Unorm);
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
            using var img = Image.Load<Rgba32>(imageData);
            var pixelBytes = new byte[img.Width * img.Height * 4];
            img.CopyPixelDataTo(pixelBytes);

            var imageWidth = (uint)img.Width;
            var imageHeight = (uint)img.Height;

            return LoadTexture(pixelBytes, imageWidth, imageHeight);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load texture for channel {channelName}: {ex.Message}");
            return null;
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
                             ?? LoadDefaultTexture(Vector4.One),
                    Normal = LoadMaterialTexture(primitive.Material, "Normal")
                             ?? LoadDefaultTexture(new Vector4(0.5f, 0.5f, 1, 1)),
                    MetallicRoughness = LoadMaterialTexture(primitive.Material, "MetallicRoughness")
                                        ?? LoadDefaultTexture(new Vector4(0, 1, 0, 0)),
                    Occlusion = LoadMaterialTexture(primitive.Material, "Occlusion")
                                ?? LoadDefaultTexture(Vector4.One),
                    Emissive = LoadMaterialTexture(primitive.Material, "Emissive")
                               ?? LoadDefaultTexture(Vector4.Zero),
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

                primitives.Add(prim);

                prim = primitives[^1];
                CreatePrimitiveDescriptorSet(ref prim, material);
                primitives[^1] = prim;
            }
        }

        var meshObj = new Mesh { Primitives = primitives };
        meshes.Add(meshObj);

        return new MeshHandle((uint)(meshes.Count - 1));
    }

    public TextureHandle LoadTexture(byte[] imageData, uint width, uint height,
        VkFormat format = VkFormat.R8G8B8A8Unorm)
    {
        var imageInfo = new VkImageCreateInfo
        {
            sType = VkStructureType.ImageCreateInfo,
            imageType = VkImageType.Image2D,
            format = format,
            extent = new VkExtent3D { width = width, height = height, depth = 1 },
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };

        var allocInfo = new VmaAllocationCreateInfo
        {
            usage = VmaMemoryUsage.AutoPreferDevice
        };

        Vma.vmaCreateImage(context.Allocator, imageInfo, allocInfo, out var image, out _, null).CheckResult();

        UploadMeshData(imageData, image, width, height, format);

        var viewInfo = new VkImageViewCreateInfo
        {
            sType = VkStructureType.ImageViewCreateInfo,
            image = image,
            viewType = VkImageViewType.Image2D,
            format = format,
            subresourceRange = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.Color,
                baseMipLevel = 0,
                levelCount = 1,
                baseArrayLayer = 0,
                layerCount = 1,
            }
        };

        context.DeviceApi.vkCreateImageView(&viewInfo, null, out var imageView).CheckResult();

        var samplerInfo = new VkSamplerCreateInfo
        {
            sType = VkStructureType.SamplerCreateInfo,
            magFilter = VkFilter.Linear,
            minFilter = VkFilter.Linear,
            mipmapMode = VkSamplerMipmapMode.Linear,
            addressModeU = VkSamplerAddressMode.Repeat,
            addressModeV = VkSamplerAddressMode.Repeat,
            addressModeW = VkSamplerAddressMode.Repeat,
            mipLodBias = 0.0f,
            anisotropyEnable = false,
            maxAnisotropy = 1.0f,
            compareEnable = false,
            minLod = 0.0f,
            maxLod = 0.0f,
        };

        context.DeviceApi.vkCreateSampler(&samplerInfo, null, out var sampler).CheckResult();

        textureImages.Add(image);
        textureImageViews.Add(imageView);
        textureSamplers.Add(sampler);

        return new TextureHandle((uint)(textureImages.Count - 1));
    }

    public TextureHandle LoadTexture(
        float[] imageData,
        uint width,
        uint height,
        VkFormat format = VkFormat.R32G32B32A32Sfloat)
    {
        var imageInfo = new VkImageCreateInfo
        {
            sType = VkStructureType.ImageCreateInfo,
            imageType = VkImageType.Image2D,
            format = format,
            extent = new VkExtent3D
            {
                width = width,
                height = height,
                depth = 1
            },
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.TransferDst |
                    VkImageUsageFlags.Sampled,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };

        var allocInfo = new VmaAllocationCreateInfo
        {
            usage = VmaMemoryUsage.AutoPreferDevice
        };

        Vma.vmaCreateImage(
            context.Allocator,
            imageInfo,
            allocInfo,
            out var image,
            out _,
            null).CheckResult();

        UploadHDRData(
            imageData,
            image,
            width,
            height,
            format);

        var viewInfo = new VkImageViewCreateInfo
        {
            sType = VkStructureType.ImageViewCreateInfo,
            image = image,
            viewType = VkImageViewType.Image2D,
            format = format,
            subresourceRange = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.Color,
                baseMipLevel = 0,
                levelCount = 1,
                baseArrayLayer = 0,
                layerCount = 1,
            }
        };

        context.DeviceApi.vkCreateImageView(
            &viewInfo,
            null,
            out var imageView).CheckResult();

        var samplerInfo = new VkSamplerCreateInfo
        {
            sType = VkStructureType.SamplerCreateInfo,
            magFilter = VkFilter.Linear,
            minFilter = VkFilter.Linear,
            mipmapMode = VkSamplerMipmapMode.Linear,
            addressModeU = VkSamplerAddressMode.Repeat,
            addressModeV = VkSamplerAddressMode.Repeat,
            addressModeW = VkSamplerAddressMode.Repeat,
            mipLodBias = 0.0f,
            anisotropyEnable = false,
            maxAnisotropy = 1.0f,
            compareEnable = false,
            minLod = 0.0f,
            maxLod = 0.0f,
        };

        context.DeviceApi.vkCreateSampler(
            &samplerInfo,
            null,
            out var sampler).CheckResult();

        textureImages.Add(image);
        textureImageViews.Add(imageView);
        textureSamplers.Add(sampler);

        return new TextureHandle(
            (uint)(textureImages.Count - 1));
    }

    public TextureHandle LoadHDRTexture(
        float[] imageData,
        uint width,
        uint height)
    {
        return LoadTexture(
            imageData,
            width,
            height,
            VkFormat.R32G32B32A32Sfloat);
    }

    private void TransitionDepthImage()
    {
        var allocInfo = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.CommandBufferAllocateInfo,
            level = VkCommandBufferLevel.Primary,
            commandPool = commandPool,
            commandBufferCount = 1,
        };

        context.DeviceApi.vkAllocateCommandBuffer(&allocInfo, out var commandBuffer).CheckResult();

        var beginInfo = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.CommandBufferBeginInfo,
            flags = VkCommandBufferUsageFlags.OneTimeSubmit
        };

        context.DeviceApi.vkBeginCommandBuffer(commandBuffer, &beginInfo).CheckResult();

        var barrier = new VkImageMemoryBarrier
        {
            sType = VkStructureType.ImageMemoryBarrier,
            srcAccessMask = 0,
            dstAccessMask = VkAccessFlags.DepthStencilAttachmentRead |
                            VkAccessFlags.DepthStencilAttachmentWrite,
            oldLayout = VkImageLayout.Undefined,
            newLayout = VkImageLayout.DepthStencilAttachmentOptimal,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = swapchain.DepthImage,
            subresourceRange = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.Depth,
                baseMipLevel = 0,
                levelCount = 1,
                baseArrayLayer = 0,
                layerCount = 1
            }
        };

        context.DeviceApi.vkCmdPipelineBarrier(
            commandBuffer,
            VkPipelineStageFlags.TopOfPipe,
            VkPipelineStageFlags.EarlyFragmentTests |
            VkPipelineStageFlags.LateFragmentTests,
            VkDependencyFlags.None,
            0, null,
            0, null,
            1, &barrier);

        context.DeviceApi.vkEndCommandBuffer(commandBuffer).CheckResult();

        var submitInfo = new VkSubmitInfo
        {
            sType = VkStructureType.SubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &commandBuffer
        };

        context.DeviceApi.vkQueueSubmit(context.GraphicsQueue, submitInfo, VkFence.Null).CheckResult();
        context.DeviceApi.vkQueueWaitIdle(context.GraphicsQueue).CheckResult();

        context.DeviceApi.vkFreeCommandBuffers(commandPool, 1, &commandBuffer);
    }

    private void CreateFrameConstantBuffer()
    {
        frameConstantBuffer = CreateBuffer(
            (ulong)sizeof(FrameConstants),
            VkBufferUsageFlags.UniformBuffer,
            VmaMemoryUsage.AutoPreferHost,
            out frameConstantAllocation
        );
    }

    private void CreateFrameDescriptorSet()
    {
        var descriptorSetLayout1 = shaderManager.GetDescriptorSetLayout("pbr_mesh", 1);

        fixed (VkDescriptorSet* pFrameDescriptorSet = &frameDescriptorSet)
        {
            var allocInfo = new VkDescriptorSetAllocateInfo
            {
                sType = VkStructureType.DescriptorSetAllocateInfo,
                descriptorPool = descriptorPool,
                descriptorSetCount = 1,
                pSetLayouts = &descriptorSetLayout1,
            };

            context.DeviceApi.vkAllocateDescriptorSets(&allocInfo, pFrameDescriptorSet).CheckResult();

            var bufferInfo = new VkDescriptorBufferInfo
            {
                buffer = frameConstantBuffer,
                offset = 0,
                range = (ulong)sizeof(FrameConstants),
            };

            var writeDescriptorSet = new VkWriteDescriptorSet
            {
                sType = VkStructureType.WriteDescriptorSet,
                dstSet = frameDescriptorSet,
                dstBinding = 0,
                dstArrayElement = 0,
                descriptorCount = 1,
                descriptorType = VkDescriptorType.UniformBuffer,
                pBufferInfo = &bufferInfo,
            };

            context.DeviceApi.vkUpdateDescriptorSets(1, &writeDescriptorSet, 0, null);
        }
    }

    private void UploadHDRData(
        float[] data,
        VkImage image,
        uint width,
        uint height,
        VkFormat format)
    {
        var byteData = MemoryMarshal.AsBytes(
            data.AsSpan());

        UploadMeshData(
            byteData.ToArray(),
            image,
            width,
            height,
            format);
    }

    private void UploadFrameConstants(FrameConstants constants)
    {
        void* mapped = null;
        Vma.vmaMapMemory(context.Allocator, frameConstantAllocation, &mapped).CheckResult();
        *(FrameConstants*)mapped = constants;
        Vma.vmaUnmapMemory(context.Allocator, frameConstantAllocation);
    }

    // Lengyel, FGED2 Vol 2 - Tangent space calculation (7.4)
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

            // Gram-Schmidt orthogonalize
            t = Vector3.Normalize(t - Vector3.Dot(t, n) * n);
            b = Vector3.Normalize(b - Vector3.Dot(b, n) * n);

            // Calculate handedness
            var handedness = Vector3.Dot(Vector3.Cross(n, t), b) < 0 ? -1.0f : 1.0f;

            result[i] = new Vector4(t.X, t.Y, t.Z, handedness);
        }

        return result;
    }

    private VkImage CreateCubemapImage(uint size)
    {
        var imageInfo = new VkImageCreateInfo
        {
            sType = VkStructureType.ImageCreateInfo,
            imageType = VkImageType.Image2D,
            format = VkFormat.R16G16B16A16Sfloat,
            extent = new VkExtent3D { width = size, height = size, depth = 1 },
            mipLevels = 1,
            arrayLayers = 6,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.Storage | VkImageUsageFlags.Sampled,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
            flags = VkImageCreateFlags.CubeCompatible,
        };

        var allocInfo = new VmaAllocationCreateInfo
        {
            usage = VmaMemoryUsage.AutoPreferDevice
        };

        Vma.vmaCreateImage(context.Allocator, imageInfo, allocInfo, out var image, out _, null).CheckResult();
        return image;
    }

    private VkImageView CreateCubemapView(VkImage image, uint size)
    {
        var viewInfo = new VkImageViewCreateInfo
        {
            sType = VkStructureType.ImageViewCreateInfo,
            image = image,
            viewType = VkImageViewType.ImageCube,
            format = VkFormat.R16G16B16A16Sfloat,
            subresourceRange = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.Color,
                baseMipLevel = 0,
                levelCount = 1,
                baseArrayLayer = 0,
                layerCount = 6,
            }
        };

        context.DeviceApi.vkCreateImageView(&viewInfo, null, out var imageView).CheckResult();
        return imageView;
    }

    private void CreateCubemapSamplers()
    {
        var cubemapSamplerInfo = new VkSamplerCreateInfo
        {
            sType = VkStructureType.SamplerCreateInfo,
            magFilter = VkFilter.Linear,
            minFilter = VkFilter.Linear,
            mipmapMode = VkSamplerMipmapMode.Linear,
            addressModeU = VkSamplerAddressMode.ClampToEdge,
            addressModeV = VkSamplerAddressMode.ClampToEdge,
            addressModeW = VkSamplerAddressMode.ClampToEdge,
            minLod = 0.0f,
            maxLod = 0.0f,
        };

        context.DeviceApi.vkCreateSampler(&cubemapSamplerInfo, null, out cubemapSampler).CheckResult();

        var brdfSamplerInfo = new VkSamplerCreateInfo
        {
            sType = VkStructureType.SamplerCreateInfo,
            magFilter = VkFilter.Linear,
            minFilter = VkFilter.Linear,
            mipmapMode = VkSamplerMipmapMode.Nearest,
            addressModeU = VkSamplerAddressMode.ClampToEdge,
            addressModeV = VkSamplerAddressMode.ClampToEdge,
            addressModeW = VkSamplerAddressMode.ClampToEdge,
            minLod = 0.0f,
            maxLod = 1.0f,
        };

        context.DeviceApi.vkCreateSampler(&brdfSamplerInfo, null, out brdfLUTSampler).CheckResult();
    }

    public CubemapHandle? LoadEquirectangularHDRI(byte[] hdrData, uint width, uint height)
    {
        try
        {
            using var imageData = Image.Load<RgbaVector>(hdrData);

            var imageWidth = (uint)imageData.Width;
            var imageHeight = (uint)imageData.Height;

            // Preserve HDR floating-point pixel data.
            var pixelData = new float[imageData.Width * imageData.Height * 4];

            imageData.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);

                    for (var x = 0; x < row.Length; x++)
                    {
                        var pixel = row[x];

                        var index = (y * row.Length + x) * 4;

                        pixelData[index + 0] = pixel.R;
                        pixelData[index + 1] = pixel.G;
                        pixelData[index + 2] = pixel.B;
                        pixelData[index + 3] = pixel.A;
                    }
                }
            });

            // This must create a floating-point Vulkan texture.
            var equirectHandle = LoadHDRTexture(
                pixelData,
                imageWidth,
                imageHeight);

            var equirectView =
                textureImageViews[(int)equirectHandle.Id];

            const uint cubeSize = 512;

            var cubemapImage = CreateCubemapImage(cubeSize);
            var cubemapView = CreateCubemapView(cubemapImage, cubeSize);

            ConvertEquirectangularToCubemap(
                equirectView,
                cubemapImage,
                cubeSize);

            cubemapTextures.Add(new CubemapData
            {
                Images = [cubemapImage],
                Views = [cubemapView],
                Width = cubeSize,
            });

            return new CubemapHandle(
                (uint)(cubemapTextures.Count - 1));
        }
        catch (Exception ex)
        {
            return null;
        }
    }

    private void ConvertEquirectangularToCubemap(VkImageView equirectView, VkImage cubemapImage, uint faceSize)
    {
        var descriptorLayout = shaderManager.GetDescriptorSetLayout("equirectangular_to_cubemap", 0);
        var allocInfo = new VkDescriptorSetAllocateInfo
        {
            sType = VkStructureType.DescriptorSetAllocateInfo,
            descriptorPool = descriptorPool,
            descriptorSetCount = 1,
            pSetLayouts = &descriptorLayout,
        };

        context.DeviceApi.vkAllocateDescriptorSets(allocInfo, out var descriptorSet).CheckResult();

        var equirectImageInfo = new VkDescriptorImageInfo
        {
            sampler = textureSamplers[0],
            imageView = equirectView,
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        var writeDescriptor = new VkWriteDescriptorSet
        {
            sType = VkStructureType.WriteDescriptorSet,
            dstSet = descriptorSet,
            dstBinding = 0,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.CombinedImageSampler,
            pImageInfo = &equirectImageInfo,
        };

        context.DeviceApi.vkUpdateDescriptorSets(1, &writeDescriptor, 0, null);

        // Transition cubemap to general layout
        TransitionImageLayout(cubemapImage, VkFormat.R16G16B16A16Sfloat, VkImageLayout.Undefined,
            VkImageLayout.General);

        // Dispatch compute for each cubemap face (array layer)
        for (var faceIndex = 0; faceIndex < 6; faceIndex++)
        {
            DispatchComputeForFace(descriptorSet, cubemapImage, faceSize, faceIndex);
        }

        // Transition to shader read
        TransitionImageLayout(cubemapImage, VkFormat.R16G16B16A16Sfloat, VkImageLayout.General,
            VkImageLayout.ShaderReadOnlyOptimal);
    }

    private void DispatchComputeForFace(VkDescriptorSet descriptorSet, VkImage cubemapImage, uint faceSize,
        int faceIndex)
    {
        // Create storage view for this array layer
        var viewInfo = new VkImageViewCreateInfo
        {
            sType = VkStructureType.ImageViewCreateInfo,
            image = cubemapImage,
            viewType = VkImageViewType.Image2D,
            format = VkFormat.R16G16B16A16Sfloat,
            subresourceRange = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.Color,
                baseMipLevel = 0,
                levelCount = 1,
                baseArrayLayer = (uint)faceIndex,
                layerCount = 1,
            }
        };

        context.DeviceApi.vkCreateImageView(&viewInfo, null, out var storageView).CheckResult();

        var storageImageInfo = new VkDescriptorImageInfo
        {
            imageView = storageView,
            imageLayout = VkImageLayout.General,
        };

        var writeStorage = new VkWriteDescriptorSet
        {
            sType = VkStructureType.WriteDescriptorSet,
            dstSet = descriptorSet,
            dstBinding = 1,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.StorageImage,
            pImageInfo = &storageImageInfo,
        };

        context.DeviceApi.vkUpdateDescriptorSets(1, &writeStorage, 0, null);

        // Allocate command buffer
        var allocInfo = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.CommandBufferAllocateInfo,
            level = VkCommandBufferLevel.Primary,
            commandPool = commandPool,
            commandBufferCount = 1,
        };

        context.DeviceApi.vkAllocateCommandBuffer(&allocInfo, out var cmdBuffer).CheckResult();

        var beginInfo = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.CommandBufferBeginInfo,
            flags = VkCommandBufferUsageFlags.OneTimeSubmit,
        };

        context.DeviceApi.vkBeginCommandBuffer(cmdBuffer, &beginInfo).CheckResult();

        var imageMemoryBarrier = new VkImageMemoryBarrier
        {
            sType = VkStructureType.ImageMemoryBarrier,
            srcAccessMask = 0,
            dstAccessMask = VkAccessFlags.ShaderWrite,
            oldLayout = VkImageLayout.General,
            newLayout = VkImageLayout.General,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            image = cubemapImage,
            subresourceRange = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.Color,
                baseMipLevel = 0,
                levelCount = 1,
                baseArrayLayer = (uint)faceIndex,
                layerCount = 1,
            }
        };

        context.DeviceApi.vkCmdPipelineBarrier(cmdBuffer,
            VkPipelineStageFlags.TopOfPipe,
            VkPipelineStageFlags.ComputeShader,
            VkDependencyFlags.None,
            0, null,
            0, null,
            1, &imageMemoryBarrier);


        context.DeviceApi.vkCmdBindPipeline(cmdBuffer, VkPipelineBindPoint.Compute,
            shaderManager.GetComputePipeline("equirectangular_to_cubemap"));
        context.DeviceApi.vkCmdBindDescriptorSets(cmdBuffer, VkPipelineBindPoint.Compute,
            shaderManager.GetPipelineLayout("equirectangular_to_cubemap"),
            0, 1, &descriptorSet, 0, null);

        var faceIndexData = faceIndex;
        context.DeviceApi.vkCmdPushConstants(cmdBuffer, shaderManager.GetPipelineLayout("equirectangular_to_cubemap"),
            VkShaderStageFlags.Compute, 0,
            (uint)sizeof(int), &faceIndexData);

        uint groupSize = 8;
        uint numGroups = (faceSize + groupSize - 1) / groupSize;
        context.DeviceApi.vkCmdDispatch(cmdBuffer, numGroups, numGroups, 1);

        context.DeviceApi.vkEndCommandBuffer(cmdBuffer).CheckResult();

        var submitInfo = new VkSubmitInfo
        {
            sType = VkStructureType.SubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &cmdBuffer,
        };

        context.DeviceApi.vkQueueSubmit(context.GraphicsQueue, submitInfo, VkFence.Null).CheckResult();
        context.DeviceApi.vkQueueWaitIdle(context.GraphicsQueue).CheckResult();

        context.DeviceApi.vkFreeCommandBuffers(commandPool, 1, &cmdBuffer);
        context.DeviceApi.vkDestroyImageView(storageView, null);
    }

    public void GenerateBRDFLUT()
    {
        const uint lutSize = 512;

        var imageInfo = new VkImageCreateInfo
        {
            sType = VkStructureType.ImageCreateInfo,
            imageType = VkImageType.Image2D,
            format = VkFormat.R16G16Sfloat,
            extent = new VkExtent3D { width = lutSize, height = lutSize, depth = 1 },
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.Storage | VkImageUsageFlags.Sampled,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };

        var allocInfo = new VmaAllocationCreateInfo
        {
            usage = VmaMemoryUsage.AutoPreferDevice
        };

        Vma.vmaCreateImage(context.Allocator, imageInfo, allocInfo, out brdfLUTImage, out _, null).CheckResult();

        var viewInfo = new VkImageViewCreateInfo
        {
            sType = VkStructureType.ImageViewCreateInfo,
            image = brdfLUTImage,
            viewType = VkImageViewType.Image2D,
            format = VkFormat.R16G16Sfloat,
            subresourceRange = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.Color,
                baseMipLevel = 0,
                levelCount = 1,
                baseArrayLayer = 0,
                layerCount = 1,
            }
        };

        context.DeviceApi.vkCreateImageView(&viewInfo, null, out brdfLUTImageView).CheckResult();

        TransitionImageLayout(brdfLUTImage, VkFormat.R16G16Sfloat, VkImageLayout.Undefined, VkImageLayout.General);

        var layout = shaderManager.GetDescriptorSetLayout("brdf_lut", 0);
        var allocDescInfo = new VkDescriptorSetAllocateInfo
        {
            sType = VkStructureType.DescriptorSetAllocateInfo,
            descriptorPool = descriptorPool,
            descriptorSetCount = 1,
            pSetLayouts = &layout
        };

        VkDescriptorSet brdfLutDescriptorSet;
        context.DeviceApi.vkAllocateDescriptorSets(&allocDescInfo, &brdfLutDescriptorSet).CheckResult();

        var storageImageInfo = new VkDescriptorImageInfo
        {
            imageView = brdfLUTImageView,
            imageLayout = VkImageLayout.General,
        };

        var writeDescriptor = new VkWriteDescriptorSet
        {
            sType = VkStructureType.WriteDescriptorSet,
            dstSet = brdfLutDescriptorSet,
            dstBinding = 0,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.StorageImage,
            pImageInfo = &storageImageInfo,
        };

        context.DeviceApi.vkUpdateDescriptorSets(1, &writeDescriptor, 0, null);

        var allocCmdInfo = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.CommandBufferAllocateInfo,
            level = VkCommandBufferLevel.Primary,
            commandPool = commandPool,
            commandBufferCount = 1,
        };

        context.DeviceApi.vkAllocateCommandBuffer(&allocCmdInfo, out var cmdBuffer).CheckResult();

        var beginInfo = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.CommandBufferBeginInfo,
            flags = VkCommandBufferUsageFlags.OneTimeSubmit,
        };

        context.DeviceApi.vkBeginCommandBuffer(cmdBuffer, &beginInfo).CheckResult();

        context.DeviceApi.vkCmdBindPipeline(cmdBuffer, VkPipelineBindPoint.Compute,
            shaderManager.GetComputePipeline("brdf_lut"));
        context.DeviceApi.vkCmdBindDescriptorSets(cmdBuffer, VkPipelineBindPoint.Compute,
            shaderManager.GetPipelineLayout("brdf_lut"),
            0, 1, &brdfLutDescriptorSet, 0, null);

        uint groupSize = 8;
        uint numGroups = (lutSize + groupSize - 1) / groupSize;
        context.DeviceApi.vkCmdDispatch(cmdBuffer, numGroups, numGroups, 1);

        context.DeviceApi.vkEndCommandBuffer(cmdBuffer).CheckResult();

        var submitInfo = new VkSubmitInfo
        {
            sType = VkStructureType.SubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &cmdBuffer,
        };

        context.DeviceApi.vkQueueSubmit(context.GraphicsQueue, submitInfo, VkFence.Null).CheckResult();
        context.DeviceApi.vkQueueWaitIdle(context.GraphicsQueue).CheckResult();

        context.DeviceApi.vkFreeCommandBuffers(commandPool, 1, &cmdBuffer);

        TransitionImageLayout(brdfLUTImage, VkFormat.R16G16Sfloat, VkImageLayout.General,
            VkImageLayout.ShaderReadOnlyOptimal);
    }
}

public struct Material
{
    public TextureHandle Albedo;
    public TextureHandle Normal;
    public TextureHandle MetallicRoughness;
    public TextureHandle Occlusion;
    public TextureHandle Emissive;
}

internal struct Primitive
{
    public VkBuffer VertexBuffer;
    public VmaAllocation VertexAllocation;
    public VkBuffer IndexBuffer;
    public VmaAllocation IndexAllocation;
    public uint IndexCount;
    public Material Material;
    public VkDescriptorSet DescriptorSet;
}

[StructLayout(LayoutKind.Sequential)]
struct FrameConstants
{
    public Vector4 CameraPos;
    public Vector4 DirectionalLight;
    public Vector4 DirectionalColor;
    public Vector4 AmbientColor;
    public uint DebugMode;
    public uint _pad1, _pad2, _pad3;
}

[StructLayout(LayoutKind.Sequential)]
struct PushConstants
{
    public Matrix4x4 Mvp;
    public Matrix4x4 Model;
}

internal struct CubemapData
{
    public VkImage[] Images;
    public VkImageView[] Views;
    public uint Width;
}