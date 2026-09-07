using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace VortexEngine.Rendering.Vulkan;

// Unsafe is unavoidable due to Vulkan API design lol
internal sealed unsafe class VulkanRenderer : IDisposable
{
    private VkInstance instance;
    private VkInstanceApi instanceApi = default!;

    private VkPhysicalDevice physicalDevice;
    private VkDevice device;
    private VkDeviceApi deviceApi = default!;

    private VkQueue graphicsQueue;
    private VkSurfaceKHR surface;
    private VkSwapchainKHR swapchain;

    private VkImage[] swapchainImages = null!;
    private VkImageView[] swapchainImageViews = null!;

    private VkShaderModule vertexShader;
    private VkShaderModule fragmentShader;
    private VkPipeline pipeline;
    private VkPipelineLayout pipelineLayout;

    private VkCommandPool commandPool;
    private VkCommandBuffer[] commandBuffers = null!;

    private VkSemaphore imageAvailableSemaphore;
    private VkSemaphore renderFinishedSemaphore;
    private VkFence inFlightFence;

    private uint graphicsQueueFamily;
    private uint presentQueueFamily;
    private uint imageCount;
    private VkExtent2D swapchainExtent;
    private VkFormat swapchainImageFormat;

    public VulkanRenderer(IVulkanSurfaceProvider surfaceProvider, uint width, uint height)
    {
        vkInitialize().CheckResult();

        swapchainExtent = new VkExtent2D { width = width, height = height };

        CreateInstance(surfaceProvider);
        surfaceProvider.CreateSurface(instance, out surface);
        SelectPhysicalDevice();
        CreateLogicalDevice();
        CreateSwapchain(width, height);
        CreateShaders();
        CreatePipelineLayout();
        CreateGraphicsPipeline();
        CreateCommandPool();
        CreateCommandBuffers();
        CreateSyncPrimitives();
    }

    private void CreateInstance(IVulkanSurfaceProvider surfaceProvider)
    {
        VkUtf8ReadOnlyString applicationName = "Vortex"u8;
        VkUtf8ReadOnlyString engineName = "Vortex Engine"u8;

        var appInfo = new VkApplicationInfo
        {
            sType = VkStructureType.ApplicationInfo,
            pApplicationName = applicationName,
            applicationVersion = new VkVersion(1, 0, 0),
            pEngineName = engineName,
            engineVersion = new VkVersion(1, 0, 0),
            apiVersion = new VkVersion(1, 4, 0)
        };

        surfaceProvider.GetRequiredExtensions(out var extensions);
        using var vkExtensions = new VkStringArray(extensions);

        var createInfo = new VkInstanceCreateInfo
        {
            sType = VkStructureType.InstanceCreateInfo,
            pApplicationInfo = &appInfo,
            enabledExtensionCount = vkExtensions.Length,
            ppEnabledExtensionNames = vkExtensions,
        };

        vkCreateInstance(&createInfo, out instance).CheckResult();

        instanceApi = GetApi(instance);
    }

    private void SelectPhysicalDevice()
    {
        uint physicalDeviceCount = 0;
        instanceApi.vkEnumeratePhysicalDevices(&physicalDeviceCount, null).CheckResult();

        if (physicalDeviceCount == 0) throw new Exception("Failed to find physical device with Vulkan support");

        Span<VkPhysicalDevice> physicalDevices = stackalloc VkPhysicalDevice[(int)physicalDeviceCount];
        instanceApi.vkEnumeratePhysicalDevices(physicalDevices).CheckResult();

        // TODO: Pick a device in a less shitty way :3
        physicalDevice = physicalDevices[0];
        FindQueueFamilies();
    }

    private void FindQueueFamilies()
    {
        uint familyCount = 0;
        instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(physicalDevice, &familyCount, null);

        var families = stackalloc VkQueueFamilyProperties[(int)familyCount];
        instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(physicalDevice, &familyCount, families);

        for (uint i = 0; i < familyCount; i++)
        {
            if ((families[i].queueFlags & VkQueueFlags.Graphics) != 0) graphicsQueueFamily = i;

            instanceApi.vkGetPhysicalDeviceSurfaceSupportKHR(physicalDevice, i, surface, out var presentSupport)
                .CheckResult();
            if (presentSupport) presentQueueFamily = i;
        }
    }

    private void CreateLogicalDevice()
    {
        float queuePriority = 1.0f;
        var queueCreateInfos = stackalloc VkDeviceQueueCreateInfo[1];
        queueCreateInfos[0] = new VkDeviceQueueCreateInfo
        {
            sType = VkStructureType.DeviceQueueCreateInfo,
            queueFamilyIndex = graphicsQueueFamily,
            queueCount = 1,
            pQueuePriorities = &queuePriority,
        };

        var extensions = new[] { "VK_KHR_swapchain", "VK_KHR_dynamic_rendering" };
        using var extensionNames = new VkStringArray(extensions);

        var features = new VkPhysicalDeviceFeatures();
        var dynamicRenderingFeatures = new VkPhysicalDeviceDynamicRenderingFeatures
        {
            sType = VkStructureType.PhysicalDeviceDynamicRenderingFeatures,
            dynamicRendering = true,
        };

        var createInfo = new VkDeviceCreateInfo
        {
            sType = VkStructureType.DeviceCreateInfo,
            queueCreateInfoCount = 1,
            pQueueCreateInfos = (VkDeviceQueueCreateInfo*)Unsafe.AsPointer(ref queueCreateInfos[0]),
            pEnabledFeatures = &features,
            enabledExtensionCount = (uint)extensions.Length,
            ppEnabledExtensionNames = extensionNames,
            pNext = &dynamicRenderingFeatures,
        };

        instanceApi.vkCreateDevice(physicalDevice, &createInfo, null, out device).CheckResult();
        deviceApi = GetApi(instance, device);
        deviceApi.vkGetDeviceQueue(graphicsQueueFamily, 0, out graphicsQueue);
    }

    private void CreateSwapchain(uint width, uint height)
    {
        instanceApi.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(physicalDevice, surface, out var capabilities)
            .CheckResult();
        uint formatCount = 0;
        instanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(physicalDevice, surface, &formatCount, null).CheckResult();
        var formats = new VkSurfaceFormatKHR[formatCount];
        instanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(physicalDevice, surface, formats).CheckResult();

        // TODO: Better format picking :3
        var surfaceFormat = formats[0];
        swapchainImageFormat = surfaceFormat.format;
        imageCount = Math.Max(2, capabilities.minImageCount);

        swapchainExtent.width = Math.Min(Math.Max(width, capabilities.minImageExtent.width),
            capabilities.maxImageExtent.width);
        swapchainExtent.height = Math.Min(Math.Max(height, capabilities.minImageExtent.height),
            capabilities.maxImageExtent.height);

        var createInfo = new VkSwapchainCreateInfoKHR
        {
            sType = VkStructureType.SwapchainCreateInfoKHR,
            surface = surface,
            minImageCount = imageCount,
            imageFormat = surfaceFormat.format,
            imageColorSpace = surfaceFormat.colorSpace,
            imageExtent = swapchainExtent,
            imageArrayLayers = 1,
            imageUsage = VkImageUsageFlags.ColorAttachment,
            imageSharingMode = VkSharingMode.Exclusive,
            preTransform = capabilities.currentTransform,
            compositeAlpha = VkCompositeAlphaFlagsKHR.Opaque,
            presentMode = VkPresentModeKHR.Fifo,
            clipped = true,
        };

        deviceApi.vkCreateSwapchainKHR(&createInfo, out swapchain).CheckResult();

        uint swapchainImageCount = 0;
        deviceApi.vkGetSwapchainImagesKHR(swapchain, &swapchainImageCount, null).CheckResult();
        swapchainImages = new VkImage[swapchainImageCount];
        deviceApi.vkGetSwapchainImagesKHR(swapchain, swapchainImages);

        swapchainImageViews = new VkImageView[swapchainImages.Length];
        for (int i = 0; i < swapchainImages.Length; i++)
        {
            var createViewInfo = new VkImageViewCreateInfo
            {
                sType = VkStructureType.ImageViewCreateInfo,
                image = swapchainImages[i],
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
                    layerCount = 1,
                }
            };

            VkImageView imageView;
            deviceApi.vkCreateImageView(&createViewInfo, null, &imageView).CheckResult();
            swapchainImageViews[i] = imageView;
        }
    }

    private void CreateShaders()
    {
        var vertexCode = ShaderCompiler.LoadSpirV("VortexEngine/Rendering/Vulkan/Shaders/triangle.vert.spv");
        vertexShader = ShaderCompiler.CreateShaderModule(deviceApi, vertexCode, "triangle.vert");
        var fragmentCode = ShaderCompiler.LoadSpirV("VortexEngine/Rendering/Vulkan/Shaders/triangle.frag.spv");
        fragmentShader = ShaderCompiler.CreateShaderModule(deviceApi, fragmentCode, "triangle.frag");
    }

    private void CreatePipelineLayout()
    {
        var pipelineLayoutInfo = new VkPipelineLayoutCreateInfo
        {
            sType = VkStructureType.PipelineLayoutCreateInfo,
            setLayoutCount = 0,
            pushConstantRangeCount = 0,
        };

        deviceApi.vkCreatePipelineLayout(&pipelineLayoutInfo, null, out pipelineLayout).CheckResult();
    }

    private void CreateGraphicsPipeline()
    {
        VkUtf8ReadOnlyString pVertexShaderStageName = "main"u8;
        var vertexShaderStage = new VkPipelineShaderStageCreateInfo
        {
            sType = VkStructureType.PipelineShaderStageCreateInfo,
            stage = VkShaderStageFlags.Vertex,
            module = vertexShader,
            pName = pVertexShaderStageName
        };

        VkUtf8String pFragmentShaderStageName = "main"u8;
        var fragmentShaderStage = new VkPipelineShaderStageCreateInfo
        {
            sType = VkStructureType.PipelineShaderStageCreateInfo,
            stage = VkShaderStageFlags.Fragment,
            module = fragmentShader,
            pName = pFragmentShaderStageName
        };
        
        var shaderStages = new[] {vertexShaderStage, fragmentShaderStage};

        var vertexInputInfo = new VkPipelineVertexInputStateCreateInfo
        {
            sType = VkStructureType.PipelineVertexInputStateCreateInfo,
            vertexBindingDescriptionCount = 0,
            vertexAttributeDescriptionCount = 0
        };

        var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo
        {
            sType = VkStructureType.PipelineInputAssemblyStateCreateInfo,
            topology = VkPrimitiveTopology.TriangleList,
            primitiveRestartEnable = false
        };

        var viewport = new VkViewport
        {
            x = 0.0f,
            y = 0.0f,
            width = (float)swapchainExtent.width,
            height = (float)swapchainExtent.height,
            minDepth = 0.0f,
            maxDepth = 1.0f,
        };

        var scissor = new VkRect2D
        {
            offset = new VkOffset2D(0, 0),
            extent = swapchainExtent
        };

        var viewportState = new VkPipelineViewportStateCreateInfo
        {
            sType = VkStructureType.PipelineViewportStateCreateInfo,
            viewportCount = 1,
            pViewports = &viewport,
            scissorCount = 1,
            pScissors = &scissor
        };

        var rasterizer = new VkPipelineRasterizationStateCreateInfo
        {
            sType = VkStructureType.PipelineRasterizationStateCreateInfo,
            depthClampEnable = false,
            rasterizerDiscardEnable = false,
            polygonMode = VkPolygonMode.Fill,
            lineWidth = 1.0f,
            cullMode = VkCullModeFlags.Back,
            frontFace = VkFrontFace.Clockwise,
            depthBiasEnable = false
        };
        
        var multisampling = new VkPipelineMultisampleStateCreateInfo
        {
            sType = VkStructureType.PipelineMultisampleStateCreateInfo,
            sampleShadingEnable = false,
            rasterizationSamples = VkSampleCountFlags.Count1
        };

        var colorBlendAttachment = new VkPipelineColorBlendAttachmentState
        {
            colorWriteMask = VkColorComponentFlags.R | VkColorComponentFlags.G | VkColorComponentFlags.B | VkColorComponentFlags.A,
            blendEnable = false
        };

        var colorBlending = new VkPipelineColorBlendStateCreateInfo
        {
            sType = VkStructureType.PipelineColorBlendStateCreateInfo,
            logicOpEnable = false,
            logicOp = VkLogicOp.Copy,
            attachmentCount = 1,
            pAttachments = &colorBlendAttachment
        };

        colorBlending.blendConstants[0] = 0.0f;
        colorBlending.blendConstants[1] = 0.0f;
        colorBlending.blendConstants[2] = 0.0f;
        colorBlending.blendConstants[3] = 0.0f;

        var colorFormat = swapchainImageFormat;
        var pipelineRenderingCreateInfo = new VkPipelineRenderingCreateInfo
        {
            sType = VkStructureType.PipelineRenderingCreateInfo,
            colorAttachmentCount = 1,
            pColorAttachmentFormats = &colorFormat
        };

        var pipelineInfo = new VkGraphicsPipelineCreateInfo
        {
            sType = VkStructureType.GraphicsPipelineCreateInfo,
            stageCount = 2,
            pVertexInputState = &vertexInputInfo,
            pInputAssemblyState = &inputAssembly,
            pViewportState = &viewportState,
            pRasterizationState = &rasterizer,
            pMultisampleState = &multisampling,
            pColorBlendState = &colorBlending,
            layout = pipelineLayout,
            pNext = &pipelineRenderingCreateInfo
        };

        fixed (VkPipelineShaderStageCreateInfo* pShaderStages = shaderStages)
        {
            pipelineInfo.pStages = pShaderStages;

            var pipelines = new VkPipeline[1];
            fixed (VkPipeline* pPipelines = pipelines)
            {
                deviceApi.vkCreateGraphicsPipelines(VkPipelineCache.Null, 1, &pipelineInfo, pPipelines).CheckResult();
            }
            pipeline = pipelines[0];
        }
    }

    private void CreateCommandPool()
    {
        var poolInfo = new VkCommandPoolCreateInfo
        {
            sType = VkStructureType.CommandPoolCreateInfo,
            flags = VkCommandPoolCreateFlags.ResetCommandBuffer,
            queueFamilyIndex = graphicsQueueFamily,
        };

        deviceApi.vkCreateCommandPool(&poolInfo, null, out commandPool).CheckResult();
    }

    private unsafe void CreateCommandBuffers()
    {
        commandBuffers = new VkCommandBuffer[swapchainImages.Length];
        var allocInfo = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.CommandBufferAllocateInfo,
            commandPool = commandPool,
            level = VkCommandBufferLevel.Primary,
            commandBufferCount = (uint)commandBuffers.Length
        };

        fixed (VkCommandBuffer* pCommandBuffers = commandBuffers)
        {
            deviceApi.vkAllocateCommandBuffers(&allocInfo, pCommandBuffers).CheckResult();
        }
    }

    private void CreateSyncPrimitives()
    {
        var semaphoreInfo = new VkSemaphoreCreateInfo
        {
            sType = VkStructureType.SemaphoreCreateInfo,
        };

        deviceApi.vkCreateSemaphore(&semaphoreInfo, null, out imageAvailableSemaphore).CheckResult();
        deviceApi.vkCreateSemaphore(&semaphoreInfo, null, out renderFinishedSemaphore).CheckResult();

        var fenceInfo = new VkFenceCreateInfo
        {
            sType = VkStructureType.FenceCreateInfo,
            flags = VkFenceCreateFlags.Signaled
        };

        deviceApi.vkCreateFence(&fenceInfo, null, out inFlightFence).CheckResult();
    }

    public void Render()
    {
        fixed (VkFence* pFence = &inFlightFence)
        {
            deviceApi.vkWaitForFences(1, pFence, true, ulong.MaxValue).CheckResult();
            deviceApi.vkResetFences(1, pFence).CheckResult();
        }

        deviceApi.vkAcquireNextImageKHR(swapchain, ulong.MaxValue, imageAvailableSemaphore, VkFence.Null,
            out var imageIndex).CheckResult();
        deviceApi.vkResetCommandBuffer(commandBuffers[imageIndex], VkCommandBufferResetFlags.None).CheckResult();

        var beginInfo = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.CommandBufferBeginInfo,
        };

        deviceApi.vkBeginCommandBuffer(commandBuffers[imageIndex], &beginInfo).CheckResult();

        var colorAttachment = new VkRenderingAttachmentInfo
        {
            sType = VkStructureType.RenderingAttachmentInfo,
            imageView = swapchainImageViews[imageIndex],
            imageLayout = VkImageLayout.ColorAttachmentOptimal,
            clearValue = new VkClearValue { color = new VkClearColorValue(0.0f, 0.0f, 0.0f, 1.0f) },
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.Store
        };

        var renderingInfo = new VkRenderingInfo
        {
            sType = VkStructureType.RenderingInfo,
            renderArea = new VkRect2D { offset = new VkOffset2D(0, 0), extent = swapchainExtent },
            layerCount = 1,
            colorAttachmentCount = 1,
            pColorAttachments = &colorAttachment
        };

        deviceApi.vkCmdBeginRendering(commandBuffers[imageIndex], &renderingInfo);

        deviceApi.vkCmdBindPipeline(commandBuffers[imageIndex], VkPipelineBindPoint.Graphics, pipeline);
        deviceApi.vkCmdDraw(commandBuffers[imageIndex], 3, 1, 0, 0);

        deviceApi.vkCmdEndRendering(commandBuffers[imageIndex]);

        deviceApi.vkEndCommandBuffer(commandBuffers[imageIndex]);

        var waitSemaphores = new[] { imageAvailableSemaphore };
        var signalSemaphores = new[] { renderFinishedSemaphore };
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

            deviceApi.vkQueueSubmit(graphicsQueue, submitInfo, inFlightFence).CheckResult();
        }

        fixed (VkSemaphore* pSignalSemaphores = signalSemaphores)
        fixed (VkSwapchainKHR* pSwapchain = &swapchain)
        {
            var presentInfo = new VkPresentInfoKHR
            {
                sType = VkStructureType.PresentInfoKHR,
                waitSemaphoreCount = 1,
                pWaitSemaphores = pSignalSemaphores,
                swapchainCount = 1,
                pSwapchains = pSwapchain,
                pImageIndices = &imageIndex,
            };

            deviceApi.vkQueuePresentKHR(graphicsQueue, &presentInfo).CheckResult();
        }
    }

    public void Dispose()
    {
    }
}