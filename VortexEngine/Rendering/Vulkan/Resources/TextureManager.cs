using System.Numerics;
using System.Runtime.InteropServices;
using StbImageSharp;
using VortexEngine.Rendering.Vulkan.Core;
using VortexEngine.Rendering.Vulkan.Shaders;
using Vortice.Vulkan;

namespace VortexEngine.Rendering.Vulkan.Resources;

internal sealed class TextureManager : IDisposable
{
    private readonly VkCommandPool commandPool;
    private readonly VulkanContext ctx;
    private readonly List<CubemapData> cubemapTextures = [];
    private readonly VkDescriptorPool descriptorPool;
    private readonly ShaderManager shaderManager;
    private readonly List<VkImageView> textureImageViews = [];
    private readonly List<VkImage> textureImages = [];
    private readonly List<VkSampler> textureSamplers = [];
    private VkImage brdfLutImage;
    private VkImageView brdfLutImageView;
    private VkSampler brdfLutSampler;
    private VkSampler cubemapSampler;

    public TextureManager(VulkanContext context, ShaderManager manager, VkDescriptorPool pool, VkCommandPool cmdPool)
    {
        ctx = context;
        shaderManager = manager;
        descriptorPool = pool;
        commandPool = cmdPool;

        CreateSamplers();
        GenerateBRDFLUT();
    }

    public CubemapHandle ActiveEnvironmentCubemap { get; set; } = new(0);

    public VkImageView BrdfLutImageView => brdfLutImageView;
    public VkSampler BrdfLutSampler => brdfLutSampler;
    public VkSampler CubemapSampler => cubemapSampler;

    public unsafe void Dispose()
    {
        foreach (var cubemap in cubemapTextures)
        {
            foreach (var view in cubemap.Views)
            {
                ctx.DeviceApi.vkDestroyImageView(view, null);
            }

            foreach (var image in cubemap.Images)
            {
                Vma.vmaDestroyImage(ctx.Allocator, image, default);
            }
        }

        foreach (var imageView in textureImageViews)
        {
            ctx.DeviceApi.vkDestroyImageView(imageView, null);
        }

        foreach (var image in textureImages)
        {
            Vma.vmaDestroyImage(ctx.Allocator, image, default);
        }

        foreach (var sampler in textureSamplers)
        {
            ctx.DeviceApi.vkDestroySampler(sampler, null);
        }

        ctx.DeviceApi.vkDestroyImageView(brdfLutImageView, null);
        Vma.vmaDestroyImage(ctx.Allocator, brdfLutImage, default);

        ctx.DeviceApi.vkDestroySampler(cubemapSampler, null);
        ctx.DeviceApi.vkDestroySampler(brdfLutSampler, null);
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

    public unsafe CubemapHandle? LoadEquirectangularHDRI(byte[] hdrData, uint width, uint height)
    {
        var image = ImageResultFloat.FromMemory(hdrData, ColorComponents.RedGreenBlueAlpha);

        var equirectHandle = LoadHDRTexture(image.Data, width, height);
        var equirectView = GetTextureImageView(equirectHandle);

        const uint faceSize = 512;

        var imageInfo = new VkImageCreateInfo
        {
            sType = VkStructureType.ImageCreateInfo,
            imageType = VkImageType.Image2D,
            format = VkFormat.R16G16B16A16Sfloat,
            extent = new VkExtent3D { width = faceSize, height = faceSize, depth = 1 },
            mipLevels = 1,
            arrayLayers = 6,
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

        Vma.vmaCreateImage(ctx.Allocator, imageInfo, allocInfo, out var cubemapImage, out _, null).CheckResult();

        var viewInfo = new VkImageViewCreateInfo
        {
            sType = VkStructureType.ImageViewCreateInfo,
            image = cubemapImage,
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

        ctx.DeviceApi.vkCreateImageView(&viewInfo, null, out var cubemapView).CheckResult();

        ConvertEquirectangularToCubemap(equirectView, cubemapImage, faceSize);

        var cubemapData = new CubemapData
        {
            Images = [cubemapImage],
            Views = [cubemapView],
            Width = faceSize,
        };

        cubemapTextures.Add(cubemapData);

        return new CubemapHandle((uint)(cubemapTextures.Count - 1));
    }

    private unsafe void ConvertEquirectangularToCubemap(VkImageView equirectView, VkImage cubemapImage, uint faceSize)
    {
        var descriptorLayout = shaderManager.GetDescriptorSetLayout("equirectangular_to_cubemap", 0);
        var allocInfo = new VkDescriptorSetAllocateInfo
        {
            sType = VkStructureType.DescriptorSetAllocateInfo,
            descriptorPool = descriptorPool,
            descriptorSetCount = 1,
            pSetLayouts = &descriptorLayout,
        };

        ctx.DeviceApi.vkAllocateDescriptorSets(allocInfo, out var descriptorSet).CheckResult();

        var equirectImageInfo = new VkDescriptorImageInfo
        {
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

        ctx.DeviceApi.vkUpdateDescriptorSets(1, &writeDescriptor, 0, null);

        TransitionImageLayout(cubemapImage, VkFormat.R16G16B16A16Sfloat, VkImageLayout.Undefined,
            VkImageLayout.General);

        for (var faceIndex = 0; faceIndex < 6; faceIndex++)
        {
            DispatchComputeForFace(descriptorSet, cubemapImage, faceSize, faceIndex);
        }

        TransitionImageLayout(cubemapImage, VkFormat.R16G16B16A16Sfloat, VkImageLayout.General,
            VkImageLayout.ShaderReadOnlyOptimal);
    }

    private unsafe void DispatchComputeForFace(VkDescriptorSet descriptorSet, VkImage cubemapImage, uint faceSize,
        int faceIndex)
    {
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

        ctx.DeviceApi.vkCreateImageView(&viewInfo, null, out var storageView).CheckResult();

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

        ctx.DeviceApi.vkUpdateDescriptorSets(1, &writeStorage, 0, null);

        var allocInfo = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.CommandBufferAllocateInfo,
            level = VkCommandBufferLevel.Primary,
            commandPool = commandPool,
            commandBufferCount = 1,
        };

        ctx.DeviceApi.vkAllocateCommandBuffer(&allocInfo, out var cmdBuffer).CheckResult();

        var beginInfo = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.CommandBufferBeginInfo,
            flags = VkCommandBufferUsageFlags.OneTimeSubmit,
        };

        ctx.DeviceApi.vkBeginCommandBuffer(cmdBuffer, &beginInfo).CheckResult();

        ctx.DeviceApi.vkCmdBindPipeline(cmdBuffer, VkPipelineBindPoint.Compute,
            shaderManager.GetComputePipeline("equirectangular_to_cubemap"));
        ctx.DeviceApi.vkCmdBindDescriptorSets(cmdBuffer, VkPipelineBindPoint.Compute,
            shaderManager.GetPipelineLayout("equirectangular_to_cubemap"),
            0, 1, &descriptorSet, 0, null);

        var faceIndexData = faceIndex;
        ctx.DeviceApi.vkCmdPushConstants(cmdBuffer, shaderManager.GetPipelineLayout("equirectangular_to_cubemap"),
            VkShaderStageFlags.Compute, 0,
            (uint)sizeof(int), &faceIndexData);

        uint groupSize = 8;
        uint numGroups = (faceSize + groupSize - 1) / groupSize;
        ctx.DeviceApi.vkCmdDispatch(cmdBuffer, numGroups, numGroups, 1);

        ctx.DeviceApi.vkEndCommandBuffer(cmdBuffer).CheckResult();

        var submitInfo = new VkSubmitInfo
        {
            sType = VkStructureType.SubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &cmdBuffer,
        };

        ctx.DeviceApi.vkQueueSubmit(ctx.GraphicsQueue, submitInfo, VkFence.Null).CheckResult();
        ctx.DeviceApi.vkQueueWaitIdle(ctx.GraphicsQueue).CheckResult();

        ctx.DeviceApi.vkFreeCommandBuffers(commandPool, 1, &cmdBuffer);
        ctx.DeviceApi.vkDestroyImageView(storageView, null);
    }

    public VkImageView GetTextureImageView(TextureHandle handle) => textureImageViews[(int)handle.Id];
    public VkSampler GetTextureSampler(TextureHandle handle) => textureSamplers[(int)handle.Id];

    public VkImageView GetCubemapImageView(CubemapHandle handle) => cubemapTextures[(int)handle.Id].Views[0];
    public VkImage GetCubemapImage(CubemapHandle handle) => cubemapTextures[(int)handle.Id].Images[0];

    private unsafe void GenerateBRDFLUT()
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

        Vma.vmaCreateImage(ctx.Allocator, imageInfo, allocInfo, out brdfLutImage, out _, null).CheckResult();

        var viewInfo = new VkImageViewCreateInfo
        {
            sType = VkStructureType.ImageViewCreateInfo,
            image = brdfLutImage,
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

        ctx.DeviceApi.vkCreateImageView(&viewInfo, null, out brdfLutImageView).CheckResult();

        TransitionImageLayout(brdfLutImage, VkFormat.R16G16Sfloat, VkImageLayout.Undefined, VkImageLayout.General);

        var layout = shaderManager.GetDescriptorSetLayout("brdf_lut", 0);
        var allocDescInfo = new VkDescriptorSetAllocateInfo
        {
            sType = VkStructureType.DescriptorSetAllocateInfo,
            descriptorPool = descriptorPool,
            descriptorSetCount = 1,
            pSetLayouts = &layout
        };

        VkDescriptorSet brdfLutDescriptorSet;
        ctx.DeviceApi.vkAllocateDescriptorSets(&allocDescInfo, &brdfLutDescriptorSet).CheckResult();

        var storageImageInfo = new VkDescriptorImageInfo
        {
            imageView = brdfLutImageView,
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

        ctx.DeviceApi.vkUpdateDescriptorSets(1, &writeDescriptor, 0, null);

        var allocCmdInfo = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.CommandBufferAllocateInfo,
            level = VkCommandBufferLevel.Primary,
            commandPool = commandPool,
            commandBufferCount = 1,
        };

        ctx.DeviceApi.vkAllocateCommandBuffer(&allocCmdInfo, out var cmdBuffer).CheckResult();

        var beginInfo = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.CommandBufferBeginInfo,
            flags = VkCommandBufferUsageFlags.OneTimeSubmit,
        };

        ctx.DeviceApi.vkBeginCommandBuffer(cmdBuffer, &beginInfo).CheckResult();

        ctx.DeviceApi.vkCmdBindPipeline(cmdBuffer, VkPipelineBindPoint.Compute,
            shaderManager.GetComputePipeline("brdf_lut"));
        ctx.DeviceApi.vkCmdBindDescriptorSets(cmdBuffer, VkPipelineBindPoint.Compute,
            shaderManager.GetPipelineLayout("brdf_lut"),
            0, 1, &brdfLutDescriptorSet, 0, null);

        uint groupSize = 8;
        uint numGroups = (lutSize + groupSize - 1) / groupSize;
        ctx.DeviceApi.vkCmdDispatch(cmdBuffer, numGroups, numGroups, 1);

        ctx.DeviceApi.vkEndCommandBuffer(cmdBuffer).CheckResult();

        var submitInfo = new VkSubmitInfo
        {
            sType = VkStructureType.SubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &cmdBuffer,
        };

        ctx.DeviceApi.vkQueueSubmit(ctx.GraphicsQueue, submitInfo, VkFence.Null).CheckResult();
        ctx.DeviceApi.vkQueueWaitIdle(ctx.GraphicsQueue).CheckResult();

        ctx.DeviceApi.vkFreeCommandBuffers(commandPool, 1, &cmdBuffer);

        TransitionImageLayout(brdfLutImage, VkFormat.R16G16Sfloat, VkImageLayout.General,
            VkImageLayout.ShaderReadOnlyOptimal);
    }

    private unsafe void CreateSamplers()
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

        ctx.DeviceApi.vkCreateSampler(&cubemapSamplerInfo, null, out cubemapSampler).CheckResult();

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

        ctx.DeviceApi.vkCreateSampler(&brdfSamplerInfo, null, out brdfLutSampler).CheckResult();
    }
}

public struct CubemapData
{
    public VkImage[] Images;
    public VkImageView[] Views;
    public uint Width;
}