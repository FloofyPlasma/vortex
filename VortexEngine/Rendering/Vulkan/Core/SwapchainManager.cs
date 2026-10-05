using Vortice.Vulkan;

namespace VortexEngine.Rendering.Vulkan.Core;

internal sealed class SwapchainManager : IDisposable
{
    private readonly VulkanContext ctx;
    private VkImage depthImage = VkImage.Null;
    private VmaAllocation depthImageAllocation;
    private VkImageView depthImageView = VkImageView.Null;
    private bool disposed;
    private VkSwapchainKHR swapchain = VkSwapchainKHR.Null;

    public SwapchainManager(VulkanContext context, uint width, uint height)
    {
        ctx = context;

        Extent = new VkExtent2D { width = width, height = height };
        CreateSwapchainResources(width, height);
        CreateDepthImage();
    }

    public VkSwapchainKHR Swapchain => swapchain;
    public VkExtent2D Extent { get; private set; }
    public VkFormat ImageFormat { get; private set; }
    public VkImageView[] ImageViews { get; private set; } = [];
    public VkImageView DepthImageView => depthImageView;
    public VkImage DepthImage => depthImage;
    public VkImage[] Images { get; private set; } = [];
    public uint ImageCount { get; private set; }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        DestroySwapchainResources();
    }

    public bool NeedsRecreate(uint width, uint height)
    {
        if (width == 0 || height == 0)
            return false;

        var extent = ClampExtent(width, height);
        return extent.width != Extent.width || extent.height != Extent.height;
    }

    public void Recreate(uint width, uint height)
    {
        if (!NeedsRecreate(width, height))
            return;

        var extent = ClampExtent(width, height);

        DestroyDepthImage();
        DestroySwapchainResources();

        CreateSwapchainResources(extent.width, extent.height);
        CreateDepthImage();
    }

    public uint AcquireNextImage(VkSemaphore imageAvailableSemaphore)
    {
        ctx.DeviceApi.vkAcquireNextImageKHR(Swapchain, ulong.MaxValue, imageAvailableSemaphore, VkFence.Null,
            out var imageIndex).CheckResult();
        return imageIndex;
    }

    public unsafe void Present(VkSemaphore renderFinishedSemaphore, uint imageIndex)
    {
        var swapchains = stackalloc VkSwapchainKHR[1];
        swapchains[0] = swapchain;
        var indices = stackalloc uint[1];
        indices[0] = imageIndex;

        var presentInfo = new VkPresentInfoKHR
        {
            sType = VkStructureType.PresentInfoKHR,
            waitSemaphoreCount = 1,
            pWaitSemaphores = &renderFinishedSemaphore,
            swapchainCount = 1,
            pSwapchains = swapchains,
            pImageIndices = indices,
        };

        ctx.DeviceApi.vkQueuePresentKHR(ctx.GraphicsQueue, &presentInfo).CheckResult();
    }

    private void DestroySwapchainResources()
    {
        unsafe
        {
            foreach (var imageView in ImageViews)
            {
                ctx.DeviceApi.vkDestroyImageView(imageView, null);
            }

            ctx.DeviceApi.vkDestroySwapchainKHR(swapchain, null);
        }

        ImageViews = [];
        Images = [];
        ImageCount = 0;
        swapchain = VkSwapchainKHR.Null;
    }

    private void DestroyDepthImage()
    {
        unsafe
        {
            ctx.DeviceApi.vkDestroyImageView(depthImageView, null);
        }

        Vma.vmaDestroyImage(ctx.Allocator, depthImage, depthImageAllocation);
        depthImageView = VkImageView.Null;
        depthImage = VkImage.Null;
    }

    private void CreateSwapchainResources(uint width, uint height)
    {
        ctx.InstanceApi.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(ctx.PhysicalDevice, ctx.Surface,
            out var capabilities).CheckResult();

        uint formatCount = 0;
        unsafe
        {
            ctx.InstanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(ctx.PhysicalDevice, ctx.Surface, &formatCount, null)
                .CheckResult();
        }

        Span<VkSurfaceFormatKHR> formats = stackalloc VkSurfaceFormatKHR[(int)formatCount];

        ctx.InstanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(ctx.PhysicalDevice, ctx.Surface, formats).CheckResult();

        // TODO: Better format picking :3
        var surfaceFormat = formats[0];
        ImageFormat = surfaceFormat.format;
        ImageCount = Math.Max(2, capabilities.minImageCount);

        var extent = ClampExtent(width, height, capabilities);
        Extent = extent;

        var createInfo = new VkSwapchainCreateInfoKHR
        {
            sType = VkStructureType.SwapchainCreateInfoKHR,
            surface = ctx.Surface,
            minImageCount = ImageCount,
            imageFormat = surfaceFormat.format,
            imageColorSpace = surfaceFormat.colorSpace,
            imageExtent = extent,
            imageArrayLayers = 1,
            imageUsage = VkImageUsageFlags.ColorAttachment,
            imageSharingMode = VkSharingMode.Exclusive,
            preTransform = capabilities.currentTransform,
            compositeAlpha = VkCompositeAlphaFlagsKHR.Opaque,
            presentMode = VkPresentModeKHR.Fifo,
            clipped = true
        };

        unsafe
        {
            ctx.DeviceApi.vkCreateSwapchainKHR(&createInfo, out swapchain).CheckResult();
        }

        uint swapchainImageCount = 0;
        unsafe
        {
            ctx.DeviceApi.vkGetSwapchainImagesKHR(Swapchain, &swapchainImageCount, null).CheckResult();
        }

        Images = new VkImage[swapchainImageCount];
        ctx.DeviceApi.vkGetSwapchainImagesKHR(Swapchain, Images).CheckResult();

        ImageViews = new VkImageView[Images.Length];
        for (var i = 0; i < Images.Length; i++)
        {
            var createViewInfo = new VkImageViewCreateInfo
            {
                sType = VkStructureType.ImageViewCreateInfo,
                image = Images[i],
                viewType = VkImageViewType.Image2D,
                format = surfaceFormat.format,
                components = new VkComponentMapping
                {
                    r = VkComponentSwizzle.Identity,
                    g = VkComponentSwizzle.Identity,
                    b = VkComponentSwizzle.Identity,
                    a = VkComponentSwizzle.Identity,
                },
                subresourceRange = new VkImageSubresourceRange
                {
                    aspectMask = VkImageAspectFlags.Color,
                    baseMipLevel = 0,
                    levelCount = 1,
                    baseArrayLayer = 0,
                    layerCount = 1
                }
            };

            unsafe
            {
                ctx.DeviceApi.vkCreateImageView(&createViewInfo, null, out var imageView).CheckResult();
                ImageViews[i] = imageView;
            }
        }
    }

    private VkExtent2D ClampExtent(uint width, uint height)
    {
        ctx.InstanceApi.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(ctx.PhysicalDevice, ctx.Surface,
            out var capabilities).CheckResult();

        return ClampExtent(width, height, capabilities);
    }

    private static VkExtent2D ClampExtent(uint width, uint height, VkSurfaceCapabilitiesKHR capabilities)
    {
        if (width == 0 || height == 0)
            return capabilities.currentExtent;

        return new VkExtent2D
        {
            width = Math.Min(Math.Max(width, capabilities.minImageExtent.width), capabilities.maxImageExtent.width),
            height = Math.Min(Math.Max(height, capabilities.minImageExtent.height), capabilities.maxImageExtent.height)
        };
    }

    private void CreateDepthImage()
    {
        var depthImageInfo = new VkImageCreateInfo
        {
            sType = VkStructureType.ImageCreateInfo,
            imageType = VkImageType.Image2D,
            format = VkFormat.D32Sfloat,
            extent = new VkExtent3D { width = Extent.width, height = Extent.height, depth = 1 },
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.DepthStencilAttachment,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };

        var allocInfo = new VmaAllocationCreateInfo
        {
            usage = VmaMemoryUsage.AutoPreferDevice
        };

        unsafe
        {
            Vma.vmaCreateImage(ctx.Allocator, depthImageInfo, allocInfo, out depthImage, out depthImageAllocation)
                .CheckResult();
        }

        var viewInfo = new VkImageViewCreateInfo
        {
            sType = VkStructureType.ImageViewCreateInfo,
            image = depthImage,
            viewType = VkImageViewType.Image2D,
            format = VkFormat.D32Sfloat,
            subresourceRange = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.Depth,
                baseMipLevel = 0,
                levelCount = 1,
                baseArrayLayer = 0,
                layerCount = 1
            }
        };

        unsafe
        {
            ctx.DeviceApi.vkCreateImageView(&viewInfo, null, out depthImageView).CheckResult();
        }
    }
}
