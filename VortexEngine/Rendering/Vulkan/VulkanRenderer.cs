using System.Numerics;
using System.Runtime.InteropServices;
using VortexEngine.Rendering.Vulkan.Core;
using VortexEngine.Rendering.Vulkan.Resources;
using VortexEngine.Rendering.Vulkan.Shaders;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace VortexEngine.Rendering.Vulkan;

// Unsafe is unavoidable due to Vulkan API design lol
internal sealed unsafe class VulkanRenderer : IDisposable
{
    private const int MaxFramesInFlight = 2;
    private readonly VulkanContext context;
    private readonly MeshManager meshManager;
    private readonly ShaderManager shaderManager;
    private readonly SwapchainManager swapchain;
    private readonly SyncManager sync;
    private readonly TextureManager textureManager;
    private VkCommandBuffer[] commandBuffers = null!;
    private VkCommandPool commandPool;
    private int currentFrame = 0;
    private VkDescriptorPool descriptorPool;
    private VmaAllocation frameConstantAllocation;
    private VkBuffer frameConstantBuffer;
    private VkDescriptorSet frameDescriptorSet;
    private float rotation;


    public VulkanRenderer(IVulkanSurfaceProvider surfaceProvider, uint width, uint height)
    {
        context = new VulkanContext(surfaceProvider);
        swapchain = new SwapchainManager(context, width, height);
        sync = new SyncManager(context);
        shaderManager = new ShaderManager(context, swapchain);

        CreateCommandPool();
        CreateDescriptorPool();
        textureManager = new TextureManager(context, shaderManager, descriptorPool, commandPool);
        meshManager = new MeshManager(context, commandPool, textureManager, shaderManager, descriptorPool);

        CreateFrameConstantBuffer();
        CreateFrameDescriptorSet();
        TransitionDepthImage();
        CreateCommandBuffers();
    }

    public void Dispose()
    {
        Vma.vmaDestroyBuffer(context.Allocator, frameConstantBuffer, frameConstantAllocation);

        meshManager.Dispose();
        textureManager.Dispose();
        shaderManager.Dispose();
        sync.Dispose();
        swapchain.Dispose();
        context.Dispose();
    }

    public void UpdateViewport(uint width, uint height)
    {
        // TODO: 
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

    public void Render(RenderRequest request)
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


        {
            // var model = Matrix4x4.CreateRotationY(rotation) * Matrix4x4.CreateRotationX(rotation) *
            // Matrix4x4.CreateScale(1.0f);

            var view = Matrix4x4.CreateLookAt(request.Camera.Position, request.Camera.Target, request.Camera.Up);

            var projection = Matrix4x4.CreatePerspectiveFieldOfView(request.Camera.FieldOfView,
                request.ViewportWidth / (float)request.ViewportHeight,
                request.Camera.Near,
                request.Camera.Far);

            context.DeviceApi.vkCmdBindPipeline(commandBuffers[imageIndex], VkPipelineBindPoint.Graphics,
                shaderManager.GetGraphicsPipeline("pbr_mesh"));

            var frameConstants = new FrameConstants
            {
                CameraPos = new Vector4(request.Camera.Position, 0),
                DirectionalLight = new Vector4(request.Light.Direction, 0),
                DirectionalColor = new Vector4(request.Light.Color, 0),
                AmbientColor = new Vector4(0.3f, 0.3f, 0.3f, 0.3f), // TODO: move to request
                DebugMode = 0,
            };

            UploadFrameConstants(frameConstants);

            fixed (VkDescriptorSet* pFrameDescriptorSet = &frameDescriptorSet)
            {
                context.DeviceApi.vkCmdBindDescriptorSets(commandBuffers[imageIndex], VkPipelineBindPoint.Graphics,
                    shaderManager.GetPipelineLayout("pbr_mesh"), 1, 1, pFrameDescriptorSet, 0, null);
            }

            foreach (var renderMesh in request.Meshes)
            {
                var mesh = meshManager.GetMesh(renderMesh.Handle);
                if (mesh.Primitives.Count == 0)
                    continue;

                var mvp = renderMesh.Transform * view * projection;

                var pushData = new PushConstants { Mvp = mvp, Model = renderMesh.Transform };
                context.DeviceApi.vkCmdPushConstants(commandBuffers[imageIndex],
                    shaderManager.GetPipelineLayout("pbr_mesh"),
                    VkShaderStageFlags.Vertex, 0,
                    (uint)sizeof(PushConstants), &pushData);

                foreach (var primitive in mesh.Primitives)
                {
                    context.DeviceApi.vkCmdBindDescriptorSets(commandBuffers[imageIndex], VkPipelineBindPoint.Graphics,
                        shaderManager.GetPipelineLayout("pbr_mesh"), 0, 1, &primitive.DescriptorSet, 0, null);

                    context.DeviceApi.vkCmdBindVertexBuffer(commandBuffers[imageIndex], 0, primitive.VertexBuffer, 0);
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

    public MeshHandle LoadMesh(byte[] meshData)
    {
        return meshManager.LoadMesh(meshData);
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

    private void UploadFrameConstants(FrameConstants constants)
    {
        void* mapped = null;
        Vma.vmaMapMemory(context.Allocator, frameConstantAllocation, &mapped).CheckResult();
        *(FrameConstants*)mapped = constants;
        Vma.vmaUnmapMemory(context.Allocator, frameConstantAllocation);
    }

    public CubemapHandle? LoadEquirectangularHDRI(byte[] hdrData, uint width, uint height)
    {
        return textureManager.LoadEquirectangularHDRI(hdrData, width, height);
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
