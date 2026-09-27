using System.Numerics;
using System.Runtime.InteropServices;
using VortexEngine.Rendering.Vulkan.Core;
using Vortice.Vulkan;

namespace VortexEngine.Rendering.Vulkan.Resources;

internal sealed class TextureManager : IDisposable
{
    private readonly VkCommandPool commandPool;
    private readonly VulkanContext ctx;
    private readonly VkDescriptorPool descriptorPool;

    private VkImage brdfLutImage;
    private VkImageView brdfLutImageView;
    private VkSampler brdfLutSampler;
    private VkSampler cubemapSampler;

    private List<CubemapData> cubemapTextures = [];
    private List<VkImageView> textureImageViews = [];

    private List<VkImage> textureImages = [];
    private List<VkSampler> textureSamplers = [];

    public TextureManager(VulkanContext context, VkDescriptorPool pool, VkCommandPool cmdPool)
    {
        ctx = context;
        descriptorPool = pool;
        commandPool = cmdPool;

        CreateSamplers();
        GenerateBRDFLUT();
    }

    public VkImageView BrdfLutImageView => brdfLutImageView;
    public VkSampler BrdfLutSampler => brdfLutSampler;
    public VkSampler CubemapSampler => cubemapSampler;

    public void Dispose()
    {
    }

    public unsafe TextureHandle LoadTexture(byte[] imageData, uint width, uint height,
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
            initialLayout = VkImageLayout.Undefined
        };

        var allocInfo = new VmaAllocationCreateInfo
        {
            usage = VmaMemoryUsage.AutoPreferDevice
        };

        Vma.vmaCreateImage(ctx.Allocator, imageInfo, allocInfo, out var image, out _, null).CheckResult();

        UploadData(imageData, image, width, height, format);

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

        ctx.DeviceApi.vkCreateImageView(&viewInfo, null, out var imageView).CheckResult();

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

        ctx.DeviceApi.vkCreateSampler(&samplerInfo, null, out var sampler).CheckResult();

        textureImages.Add(image);
        textureImageViews.Add(imageView);
        textureSamplers.Add(sampler);

        return new TextureHandle((uint)(textureImages.Count - 1));
    }

    public TextureHandle LoadDefaultTexture(Vector4 color)
    {
        var r = (byte)(color.X * 255);
        var g = (byte)(color.Y * 255);
        var b = (byte)(color.Z * 255);
        var a = (byte)(color.W * 255);

        byte[] pixelData = [r, g, b, a];

        return LoadTexture(pixelData, 1, 1, VkFormat.R8G8B8A8Unorm);
    }


    public TextureHandle LoadHDRTexture(
        float[] imageData,
        uint width,
        uint height)
    {
        return LoadTexture(
            imageData,
            width,
            height);
    }

    private unsafe TextureHandle LoadTexture(
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
            ctx.Allocator,
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

        ctx.DeviceApi.vkCreateImageView(
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

        ctx.DeviceApi.vkCreateSampler(
            &samplerInfo,
            null,
            out var sampler).CheckResult();

        textureImages.Add(image);
        textureImageViews.Add(imageView);
        textureSamplers.Add(sampler);

        return new TextureHandle(
            (uint)(textureImages.Count - 1));
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

        UploadData(
            [.. byteData],
            image,
            width,
            height,
            format);
    }


    private unsafe VkBuffer CreateBuffer(ulong size, VkBufferUsageFlags usage, VmaMemoryUsage memoryUsage,
        out VmaAllocation allocation)
    {
        var bufferInfo = new VkBufferCreateInfo
        {
            sType = VkStructureType.BufferCreateInfo,
            usage = usage,
            size = size,
        };

        var allocInfo = new VmaAllocationCreateInfo
        {
            usage = memoryUsage,
            flags = VmaAllocationCreateFlags.HostAccessSequentialWrite
        };

        Vma.vmaCreateBuffer(ctx.Allocator, bufferInfo, allocInfo, out var buffer, out allocation).CheckResult();

        return buffer;
    }

    private unsafe void CopyBufferToImage(VkBuffer buffer, VkImage image, uint width, uint height)
    {
        var allocInfo = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.CommandBufferAllocateInfo,
            level = VkCommandBufferLevel.Primary,
            commandPool = commandPool,
            commandBufferCount = 1
        };

        ctx.DeviceApi.vkAllocateCommandBuffer(&allocInfo, out var cmd).CheckResult();

        var beginInfo = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.CommandBufferBeginInfo,
            flags = VkCommandBufferUsageFlags.OneTimeSubmit
        };

        ctx.DeviceApi.vkBeginCommandBuffer(cmd, &beginInfo).CheckResult();

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

        ctx.DeviceApi.vkCmdCopyBufferToImage(cmd, buffer, image, VkImageLayout.TransferDstOptimal, 1, &region);

        ctx.DeviceApi.vkEndCommandBuffer(cmd).CheckResult();

        var submitInfo = new VkSubmitInfo
        {
            sType = VkStructureType.SubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &cmd
        };

        ctx.DeviceApi.vkQueueSubmit(ctx.GraphicsQueue, submitInfo, VkFence.Null).CheckResult();
        ctx.DeviceApi.vkQueueWaitIdle(ctx.GraphicsQueue).CheckResult();

        ctx.DeviceApi.vkFreeCommandBuffers(commandPool, 1, &cmd);
    }

    private unsafe void TransitionImageLayout(VkImage image, VkFormat format, VkImageLayout oldLayout,
        VkImageLayout newLayout)
    {
        var allocInfo = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.CommandBufferAllocateInfo,
            level = VkCommandBufferLevel.Primary,
            commandPool = commandPool,
            commandBufferCount = 1,
        };

        ctx.DeviceApi.vkAllocateCommandBuffer(&allocInfo, out var cmd).CheckResult();

        var beginInfo = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.CommandBufferBeginInfo,
            flags = VkCommandBufferUsageFlags.OneTimeSubmit
        };

        ctx.DeviceApi.vkBeginCommandBuffer(cmd, &beginInfo).CheckResult();

        var barrier = new VkImageMemoryBarrier
        {
            sType = VkStructureType.ImageMemoryBarrier,
            oldLayout = oldLayout,
            newLayout = newLayout,
            srcQueueFamilyIndex = Vortice.Vulkan.Vulkan.VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = Vortice.Vulkan.Vulkan.VK_QUEUE_FAMILY_IGNORED,
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

        ctx.DeviceApi.vkCmdPipelineBarrier(cmd, srcStage, dstStage, VkDependencyFlags.None, 0, null, 0, null, 1,
            &barrier);

        ctx.DeviceApi.vkEndCommandBuffer(cmd).CheckResult();

        var submitInfo = new VkSubmitInfo
        {
            sType = VkStructureType.SubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &cmd
        };

        ctx.DeviceApi.vkQueueSubmit(ctx.GraphicsQueue, submitInfo, VkFence.Null).CheckResult();
        ctx.DeviceApi.vkQueueWaitIdle(ctx.GraphicsQueue).CheckResult();

        ctx.DeviceApi.vkFreeCommandBuffers(commandPool, 1, &cmd);
    }

    private unsafe void UploadData(ReadOnlySpan<byte> data, VkImage dstImage, uint width, uint height, VkFormat format)
    {
        var stagingBuffer = CreateBuffer((ulong)data.Length, VkBufferUsageFlags.TransferSrc,
            VmaMemoryUsage.AutoPreferHost, out var stagingAlloc);

        void* mapped = null;
        Vma.vmaMapMemory(ctx.Allocator, stagingAlloc, &mapped).CheckResult();
        data.CopyTo(new Span<byte>(mapped, data.Length));
        Vma.vmaUnmapMemory(ctx.Allocator, stagingAlloc);

        TransitionImageLayout(dstImage, format, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal);

        CopyBufferToImage(stagingBuffer, dstImage, width, height);

        TransitionImageLayout(dstImage, format, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal);

        Vma.vmaDestroyBuffer(ctx.Allocator, stagingBuffer, stagingAlloc);
    }

    public CubemapHandle? LoadEquirectangularHDRI(byte[] hdrData, uint width, uint height)
    {
        return null;
    }

    public VkImageView GetTextureImageView(TextureHandle handle) => textureImageViews[(int)handle.Id];
    public VkSampler GetTextureSampler(TextureHandle handle) => textureSamplers[(int)handle.Id];

    public VkImageView GetCubemapImageView(CubemapHandle handle) => cubemapTextures[(int)handle.Id].Views[0];
    public VkImage GetCubemapImage(CubemapHandle handle) => cubemapTextures[(int)handle.Id].Images[0];

    private void GenerateBRDFLUT()
    {
    }

    private void CreateSamplers()
    {
    }
}

public struct CubemapData
{
    public VkImage[] Images;
    public VkImageView[] Views;
    public uint Width;
}