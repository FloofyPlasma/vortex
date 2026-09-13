using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp.PixelFormats;
using Vortice.ShaderCompiler;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;
using Image = SixLabors.ImageSharp.Image;

namespace VortexEngine.Rendering.Vulkan;

// Unsafe is unavoidable due to Vulkan API design lol
internal sealed unsafe class VulkanRenderer : IDisposable
{
    private const int MaxFramesInFlight = 2;
    private VmaAllocator allocator;
    private VkCommandBuffer[] commandBuffers = null!;

    private VkCommandPool commandPool;
    private int currentFrame = 0;

    private VkImage depthImage;
    private VmaAllocation depthImageAllocation;
    private VkImageView depthImageView;

    private VkDescriptorPool descriptorPool;
    private VkDescriptorSetLayout descriptorSetLayout0;
    private VkDescriptorSetLayout descriptorSetLayout1;
    private VkDevice device;
    private VkDeviceApi deviceApi = default!;
    private VmaAllocation frameConstantAllocation;

    private VkBuffer frameConstantBuffer;
    private VkDescriptorSet frameDescriptorSet;

    private VkQueue graphicsQueue;

    private uint graphicsQueueFamily;
    private VkSemaphore[] imageAvailableSemaphores = null!;
    private uint imageCount;

    private VkFence[] inFlightFences = null!;
    private VkInstance instance;
    private VkInstanceApi instanceApi = default!;
    private VkShaderModule meshFragmentShader;
    private VkPipeline meshPipeline;
    private VkShaderModule meshVertexShader;

    private List<Mesh> meshes = [];

    private VkPhysicalDevice physicalDevice;
    private VkPipelineLayout pipelineLayout;
    private uint presentQueueFamily;
    private VkSemaphore[] renderFinishedSemaphores = null!;

    // Debug Mesh Stuff
    private float rotation;
    private VkSurfaceKHR surface;
    private VkSwapchainKHR swapchain;
    private VkExtent2D swapchainExtent;
    private VkFormat swapchainImageFormat;
    private VkImageView[] swapchainImageViews = null!;

    private VkImage[] swapchainImages = null!;
    private List<VkImageView> textureImageViews = [];
    private List<VkImage> textureImages = [];
    private List<VkSampler> textureSamplers = [];
    private VkShaderModule triangleFragmentShader;
    private VkPipeline trianglePipeline;

    private VkShaderModule triangleVertexShader;

    public VulkanRenderer(IVulkanSurfaceProvider surfaceProvider, uint width, uint height)
    {
        vkInitialize().CheckResult();

        swapchainExtent = new VkExtent2D { width = width, height = height };

        CreateInstance(surfaceProvider);
        surfaceProvider.CreateSurface(instance, out surface);
        SelectPhysicalDevice();
        CreateLogicalDevice();
        CreateAllocator();
        CreateSwapchain(width, height);
        CreateShaders();
        CreateDescriptorSetLayout();
        CreateDescriptorPool();
        CreateFrameConstantBuffer();
        CreateFrameDescriptorSet();
        CreatePipelineLayout();
        CreateTriangleGraphicsPipeline();
        CreateMeshGraphicsPipeline();
        CreateCommandPool();
        TransitionDepthImage();
        CreateCommandBuffers();
        CreateSyncPrimitives();
    }

    public void Dispose()
    {
        foreach (var mesh in meshes)
        {
            foreach (var primitive in mesh.Primitives)
            {
                Vma.vmaDestroyBuffer(allocator, primitive.VertexBuffer, primitive.VertexAllocation);
                Vma.vmaDestroyBuffer(allocator, primitive.IndexBuffer, primitive.IndexAllocation);
            }
        }

        deviceApi.vkDestroyImageView(depthImageView, null);

        deviceApi.vkDestroyDescriptorPool(descriptorPool, null);
        deviceApi.vkDestroyDescriptorSetLayout(descriptorSetLayout0, null);

        Vma.vmaDestroyImage(allocator, depthImage, depthImageAllocation);
        Vma.vmaDestroyBuffer(allocator, frameConstantBuffer, frameConstantAllocation);

        Vma.vmaDestroyAllocator(allocator);
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

        // Depth image 
        var depthImageInfo = new VkImageCreateInfo
        {
            sType = VkStructureType.ImageCreateInfo,
            imageType = VkImageType.Image2D,
            format = VkFormat.D32Sfloat,
            extent = new VkExtent3D { width = swapchainExtent.width, height = swapchainExtent.height, depth = 1 },
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
            usage = VmaMemoryUsage.AutoPreferDevice,
        };

        Vma.vmaCreateImage(allocator, depthImageInfo, allocInfo, out depthImage, out depthImageAllocation)
            .CheckResult();

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
                layerCount = 1,
            }
        };

        deviceApi.vkCreateImageView(&viewInfo, null, out depthImageView).CheckResult();
    }

    private void CreateShaders()
    {
        var triangleVertexCode = ShaderCompiler.LoadAndCompileGlsl(
            "VortexEngine/Rendering/Vulkan/Shaders/triangle.vert",
            ShaderKind.VertexShader);
        triangleVertexShader = ShaderCompiler.CreateShaderModule(deviceApi, triangleVertexCode, "triangle.vert");
        var triangleFragmentCode = ShaderCompiler.LoadAndCompileGlsl(
            "VortexEngine/Rendering/Vulkan/Shaders/triangle.frag",
            ShaderKind.FragmentShader);
        triangleFragmentShader = ShaderCompiler.CreateShaderModule(deviceApi, triangleFragmentCode, "triangle.frag");

        var meshVertexCode = ShaderCompiler.LoadAndCompileGlsl("VortexEngine/Rendering/Vulkan/Shaders/mesh.vert",
            ShaderKind.VertexShader);
        meshVertexShader = ShaderCompiler.CreateShaderModule(deviceApi, meshVertexCode, "mesh.vert");
        var meshFragmentCode = ShaderCompiler.LoadAndCompileGlsl("VortexEngine/Rendering/Vulkan/Shaders/mesh.frag",
            ShaderKind.FragmentShader);
        meshFragmentShader = ShaderCompiler.CreateShaderModule(deviceApi, meshFragmentCode, "mesh.frag");
    }

    private void CreatePipelineLayout()
    {
        var layouts = stackalloc VkDescriptorSetLayout[2];
        layouts[0] = descriptorSetLayout0;
        layouts[1] = descriptorSetLayout1;

        var pushConstantRange = new VkPushConstantRange
        {
            stageFlags = VkShaderStageFlags.Vertex,
            offset = 0,
            size = (uint)sizeof(Matrix4x4) * 2
        };

        var pipelineLayoutInfo = new VkPipelineLayoutCreateInfo
        {
            sType = VkStructureType.PipelineLayoutCreateInfo,
            setLayoutCount = 2,
            pSetLayouts = layouts,
            pushConstantRangeCount = 1,
            pPushConstantRanges = &pushConstantRange,
        };

        deviceApi.vkCreatePipelineLayout(&pipelineLayoutInfo, null, out pipelineLayout).CheckResult();
    }

    private void CreateTriangleGraphicsPipeline()
    {
        VkUtf8ReadOnlyString pVertexShaderStageName = "main"u8;
        var vertexShaderStage = new VkPipelineShaderStageCreateInfo
        {
            sType = VkStructureType.PipelineShaderStageCreateInfo,
            stage = VkShaderStageFlags.Vertex,
            module = triangleVertexShader,
            pName = pVertexShaderStageName
        };

        VkUtf8String pFragmentShaderStageName = "main"u8;
        var fragmentShaderStage = new VkPipelineShaderStageCreateInfo
        {
            sType = VkStructureType.PipelineShaderStageCreateInfo,
            stage = VkShaderStageFlags.Fragment,
            module = triangleFragmentShader,
            pName = pFragmentShaderStageName
        };

        var shaderStages = new[] { vertexShaderStage, fragmentShaderStage };

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
            colorWriteMask = VkColorComponentFlags.R | VkColorComponentFlags.G | VkColorComponentFlags.B |
                             VkColorComponentFlags.A,
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

        var depthStencil = new VkPipelineDepthStencilStateCreateInfo
        {
            sType = VkStructureType.PipelineDepthStencilStateCreateInfo,
            depthTestEnable = true,
            depthWriteEnable = true,
            depthCompareOp = VkCompareOp.Less,
            depthBoundsTestEnable = false,
            stencilTestEnable = false,
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
            pNext = &pipelineRenderingCreateInfo,
            pDepthStencilState = &depthStencil,
        };

        fixed (VkPipelineShaderStageCreateInfo* pShaderStages = shaderStages)
        {
            pipelineInfo.pStages = pShaderStages;

            var pipelines = new VkPipeline[1];
            fixed (VkPipeline* pPipelines = pipelines)
            {
                deviceApi.vkCreateGraphicsPipelines(VkPipelineCache.Null, 1, &pipelineInfo, pPipelines).CheckResult();
            }

            trianglePipeline = pipelines[0];
        }
    }

    private void CreateMeshGraphicsPipeline()
    {
        VkUtf8ReadOnlyString pVertexShaderStageName = "main"u8;
        var vertexShaderStage = new VkPipelineShaderStageCreateInfo
        {
            sType = VkStructureType.PipelineShaderStageCreateInfo,
            stage = VkShaderStageFlags.Vertex,
            module = meshVertexShader,
            pName = pVertexShaderStageName
        };

        VkUtf8String pFragmentShaderStageName = "main"u8;
        var fragmentShaderStage = new VkPipelineShaderStageCreateInfo
        {
            sType = VkStructureType.PipelineShaderStageCreateInfo,
            stage = VkShaderStageFlags.Fragment,
            module = meshFragmentShader,
            pName = pFragmentShaderStageName
        };

        var shaderStages = new[] { vertexShaderStage, fragmentShaderStage };

        var bindingDescription = new VkVertexInputBindingDescription
        {
            binding = 0,
            stride = (uint)sizeof(Vertex),
            inputRate = VkVertexInputRate.Vertex
        };

        var attributeDescriptions = stackalloc VkVertexInputAttributeDescription[4];
        attributeDescriptions[0] = new VkVertexInputAttributeDescription
        {
            location = 0,
            binding = 0,
            format = VkFormat.R32G32B32Sfloat,
            offset = (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Position))
        };
        attributeDescriptions[1] = new VkVertexInputAttributeDescription
        {
            location = 1,
            binding = 0,
            format = VkFormat.R32G32B32Sfloat,
            offset = (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Normal))
        };
        attributeDescriptions[2] = new VkVertexInputAttributeDescription
        {
            location = 2,
            binding = 0,
            format = VkFormat.R32G32Sfloat,
            offset = (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.TexCoord))
        };
        attributeDescriptions[3] = new VkVertexInputAttributeDescription
        {
            location = 3,
            binding = 0,
            format = VkFormat.R32G32B32A32Sfloat,
            offset = (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Tangent))
        };

        Console.WriteLine($"Position offset: {Marshal.OffsetOf<Vertex>(nameof(Vertex.Position))}");
        Console.WriteLine($"Normal offset: {Marshal.OffsetOf<Vertex>(nameof(Vertex.Normal))}");
        Console.WriteLine($"TexCoord offset: {Marshal.OffsetOf<Vertex>(nameof(Vertex.TexCoord))}");
        Console.WriteLine($"Tangent offset: {Marshal.OffsetOf<Vertex>(nameof(Vertex.Tangent))}");
        Console.WriteLine($"Stride: {sizeof(Vertex)}");

        var vertexInputInfo = new VkPipelineVertexInputStateCreateInfo
        {
            sType = VkStructureType.PipelineVertexInputStateCreateInfo,
            vertexBindingDescriptionCount = 1,
            pVertexBindingDescriptions = &bindingDescription,
            vertexAttributeDescriptionCount = 4,
            pVertexAttributeDescriptions = attributeDescriptions
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
            y = swapchainExtent.height,
            width = (float)swapchainExtent.width,
            height = (float)-swapchainExtent.height,
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
            frontFace = VkFrontFace.CounterClockwise,
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
            colorWriteMask = VkColorComponentFlags.R |
                             VkColorComponentFlags.G |
                             VkColorComponentFlags.B |
                             VkColorComponentFlags.A,
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
            pColorAttachmentFormats = &colorFormat,
            depthAttachmentFormat = VkFormat.D32Sfloat
        };

        var depthStencil = new VkPipelineDepthStencilStateCreateInfo
        {
            sType = VkStructureType.PipelineDepthStencilStateCreateInfo,
            depthTestEnable = true,
            depthWriteEnable = true,
            depthCompareOp = VkCompareOp.LessOrEqual,
            depthBoundsTestEnable = false,
            stencilTestEnable = false,
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
            pNext = &pipelineRenderingCreateInfo,
            pDepthStencilState = &depthStencil
        };

        fixed (VkPipelineShaderStageCreateInfo* pShaderStages = shaderStages)
        {
            pipelineInfo.pStages = pShaderStages;

            var pipelines = new VkPipeline[1];

            fixed (VkPipeline* pPipelines = pipelines)
            {
                deviceApi.vkCreateGraphicsPipelines(
                    VkPipelineCache.Null,
                    1,
                    &pipelineInfo,
                    pPipelines).CheckResult();
            }

            meshPipeline = pipelines[0];
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
        inFlightFences = new VkFence[MaxFramesInFlight];
        imageAvailableSemaphores = new VkSemaphore[MaxFramesInFlight];
        renderFinishedSemaphores = new VkSemaphore[MaxFramesInFlight];


        var semaphoreInfo = new VkSemaphoreCreateInfo
        {
            sType = VkStructureType.SemaphoreCreateInfo,
        };

        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            deviceApi.vkCreateSemaphore(&semaphoreInfo, null, out imageAvailableSemaphores[i]).CheckResult();
            deviceApi.vkCreateSemaphore(&semaphoreInfo, null, out renderFinishedSemaphores[i]).CheckResult();

            var fenceInfo = new VkFenceCreateInfo
            {
                sType = VkStructureType.FenceCreateInfo,
                flags = VkFenceCreateFlags.Signaled
            };

            deviceApi.vkCreateFence(&fenceInfo, null, out inFlightFences[i]).CheckResult();
        }
    }

    public void Render()
    {
        var fence = inFlightFences[currentFrame];
        var imageAvail = imageAvailableSemaphores[currentFrame];
        var renderDone = renderFinishedSemaphores[currentFrame];

        deviceApi.vkWaitForFences(1, &fence, true, ulong.MaxValue).CheckResult();
        deviceApi.vkResetFences(1, &fence).CheckResult();

        deviceApi.vkAcquireNextImageKHR(swapchain, ulong.MaxValue, imageAvail, VkFence.Null,
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

        var depthAttachment = new VkRenderingAttachmentInfo
        {
            sType = VkStructureType.RenderingAttachmentInfo,
            imageView = depthImageView,
            imageLayout = VkImageLayout.DepthStencilAttachmentOptimal,
            clearValue = new VkClearValue { depthStencil = new VkClearDepthStencilValue(depth: 1.0f, stencil: 0) },
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.DontCare,
        };

        var renderingInfo = new VkRenderingInfo
        {
            sType = VkStructureType.RenderingInfo,
            renderArea = new VkRect2D { offset = new VkOffset2D(0, 0), extent = swapchainExtent },
            layerCount = 1,
            colorAttachmentCount = 1,
            pColorAttachments = &colorAttachment,
            pDepthAttachment = &depthAttachment,
        };

        deviceApi.vkCmdBeginRendering(commandBuffers[imageIndex], &renderingInfo);


        deviceApi.vkCmdBindPipeline(commandBuffers[imageIndex], VkPipelineBindPoint.Graphics, trianglePipeline);

        if (meshes.Count > 0)
        {
            {
                var model = Matrix4x4.CreateRotationY(rotation) * Matrix4x4.CreateRotationX(rotation) *
                            Matrix4x4.CreateScale(15.0f);


                var view = Matrix4x4.CreateLookAt(
                    new Vector3(0, 2, 3),
                    Vector3.Zero,
                    Vector3.UnitY
                );

                var projection = Matrix4x4.CreatePerspectiveFieldOfView(
                    MathF.PI / 4.0f,
                    swapchainExtent.width / (float)swapchainExtent.height,
                    0.1f,
                    100.0f
                );

                var mvp = model * view * projection;

                var mesh = meshes[0];
                var offset = 0UL;
                deviceApi.vkCmdBindPipeline(commandBuffers[imageIndex], VkPipelineBindPoint.Graphics, meshPipeline);

                var pushData = new PushConstants { Mvp = mvp, Model = model };
                deviceApi.vkCmdPushConstants(commandBuffers[imageIndex], pipelineLayout, VkShaderStageFlags.Vertex, 0,
                    (uint)sizeof(PushConstants), &pushData);

                var frameConstants = new FrameConstants
                {
                    CameraPos = new Vector4(0, 2, 3, 0),
                    DirectionalLight = new Vector4(0, -2, -3, 1),
                    DirectionalColor = new Vector4(1, 1, 1, 1),
                    AmbientColor = new Vector4(0.3f, 0.3f, 0.3f, 0.3f),
                    DebugMode = 0, // 0 = full PBR, 1 = metallic, 2 = roughness, 3 = normal, 4 = AO
                };

                UploadFrameConstants(frameConstants);

                fixed (VkDescriptorSet* pFrameDescriptorSet = &frameDescriptorSet)
                {
                    deviceApi.vkCmdBindDescriptorSets(commandBuffers[imageIndex], VkPipelineBindPoint.Graphics,
                        pipelineLayout, 1, 1, pFrameDescriptorSet, 0, null);
                }

                foreach (var primitive in mesh.Primitives)
                {
                    deviceApi.vkCmdBindDescriptorSets(commandBuffers[imageIndex], VkPipelineBindPoint.Graphics,
                        pipelineLayout, 0, 1, &primitive.DescriptorSet, 0, null);

                    deviceApi.vkCmdBindVertexBuffer(commandBuffers[imageIndex], 0, primitive.VertexBuffer, offset);
                    deviceApi.vkCmdBindIndexBuffer(commandBuffers[imageIndex], primitive.IndexBuffer, 0,
                        VkIndexType.Uint32);
                    deviceApi.vkCmdDrawIndexed(commandBuffers[imageIndex], primitive.IndexCount, 1, 0, 0, 0);
                }
            }
        }
        else
        {
            deviceApi.vkCmdBindPipeline(commandBuffers[imageIndex], VkPipelineBindPoint.Graphics, trianglePipeline);

            deviceApi.vkCmdDraw(commandBuffers[imageIndex], 3, 1, 0, 0);
        }

        deviceApi.vkCmdEndRendering(commandBuffers[imageIndex]);

        deviceApi.vkEndCommandBuffer(commandBuffers[imageIndex]);

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

            deviceApi.vkQueueSubmit(graphicsQueue, submitInfo, fence).CheckResult();
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

        currentFrame = (currentFrame + 1) % MaxFramesInFlight;
        rotation = (rotation + 0.01f) % (2.0f * MathF.PI);
    }

    private void CreateAllocator()
    {
        var allocatorInfo = new VmaAllocatorCreateInfo
        {
            physicalDevice = physicalDevice,
            device = device,
            instance = instance,
        };

        Vma.vmaCreateAllocator(&allocatorInfo, out allocator).CheckResult();
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

        Vma.vmaCreateBuffer(allocator, bufferInfo, allocInfo, out var buffer, out allocation, null).CheckResult();

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

        deviceApi.vkAllocateCommandBuffer(&allocInfo, out var copyCmd).CheckResult();

        var beginInfo = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.CommandBufferBeginInfo,
            flags = VkCommandBufferUsageFlags.OneTimeSubmit
        };

        deviceApi.vkBeginCommandBuffer(copyCmd, &beginInfo).CheckResult();

        var copyRegion = new VkBufferCopy
        {
            srcOffset = 0,
            dstOffset = 0,
            size = size,
        };

        deviceApi.vkCmdCopyBuffer(copyCmd, srcBuffer, dstBuffer, 1, &copyRegion);

        deviceApi.vkEndCommandBuffer(copyCmd).CheckResult();

        var submitInfo = new VkSubmitInfo
        {
            sType = VkStructureType.SubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &copyCmd
        };

        deviceApi.vkQueueSubmit(graphicsQueue, submitInfo, VkFence.Null).CheckResult();
        deviceApi.vkQueueWaitIdle(graphicsQueue).CheckResult();

        deviceApi.vkFreeCommandBuffers(commandPool, 1, &copyCmd);
    }

    private void UploadMeshData(ReadOnlySpan<byte> data, VkBuffer dstBuffer, ulong offset)
    {
        var stagingBuffer = CreateBuffer((ulong)data.Length,
            VkBufferUsageFlags.TransferSrc,
            VmaMemoryUsage.AutoPreferHost,
            out var stagingAlloc);

        void* mapped = null;
        var mapResult = Vma.vmaMapMemory(allocator, stagingAlloc, &mapped);
        if (mapResult != VkResult.Success)
            throw new Exception($"Failed to map memory: {mapResult}");

        data.CopyTo(new Span<byte>(mapped, data.Length));
        Vma.vmaUnmapMemory(allocator, stagingAlloc);

        CopyBuffer(stagingBuffer, dstBuffer, (ulong)data.Length);

        Vma.vmaDestroyBuffer(allocator, stagingBuffer, stagingAlloc);
    }

    private void UploadMeshData(ReadOnlySpan<byte> data, VkImage dstImage, uint width, uint height, VkFormat format)
    {
        var stagingBuffer = CreateBuffer((ulong)data.Length, VkBufferUsageFlags.TransferSrc,
            VmaMemoryUsage.AutoPreferHost, out var stagingAlloc);

        void* mapped = null;
        Vma.vmaMapMemory(allocator, stagingAlloc, &mapped).CheckResult();
        data.CopyTo(new Span<byte>(mapped, data.Length));
        Vma.vmaUnmapMemory(allocator, stagingAlloc);

        TransitionImageLayout(dstImage, format, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal);

        CopyBufferToImage(stagingBuffer, dstImage, width, height);

        TransitionImageLayout(dstImage, format, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal);

        Vma.vmaDestroyBuffer(allocator, stagingBuffer, stagingAlloc);
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

        deviceApi.vkAllocateCommandBuffer(&allocInfo, out var cmd).CheckResult();

        var beginInfo = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.CommandBufferBeginInfo,
            flags = VkCommandBufferUsageFlags.OneTimeSubmit
        };

        deviceApi.vkBeginCommandBuffer(cmd, &beginInfo).CheckResult();

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

        deviceApi.vkCmdPipelineBarrier(cmd, srcStage, dstStage, VkDependencyFlags.None, 0, null, 0, null, 1, &barrier);

        deviceApi.vkEndCommandBuffer(cmd).CheckResult();

        var submitInfo = new VkSubmitInfo
        {
            sType = VkStructureType.SubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &cmd
        };

        deviceApi.vkQueueSubmit(graphicsQueue, submitInfo, VkFence.Null).CheckResult();
        deviceApi.vkQueueWaitIdle(graphicsQueue).CheckResult();

        deviceApi.vkFreeCommandBuffers(commandPool, 1, &cmd);
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

        deviceApi.vkAllocateCommandBuffer(&allocInfo, out var cmd).CheckResult();

        var beginInfo = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.CommandBufferBeginInfo,
            flags = VkCommandBufferUsageFlags.OneTimeSubmit
        };

        deviceApi.vkBeginCommandBuffer(cmd, &beginInfo).CheckResult();

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

        deviceApi.vkCmdCopyBufferToImage(cmd, buffer, image, VkImageLayout.TransferDstOptimal, 1, &region);

        deviceApi.vkEndCommandBuffer(cmd).CheckResult();

        var submitInfo = new VkSubmitInfo
        {
            sType = VkStructureType.SubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &cmd
        };

        deviceApi.vkQueueSubmit(graphicsQueue, submitInfo, VkFence.Null).CheckResult();
        deviceApi.vkQueueWaitIdle(graphicsQueue).CheckResult();

        deviceApi.vkFreeCommandBuffers(commandPool, 1, &cmd);
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

    private void CreatePrimitiveDescriptorSet(ref Primitive primitive, Material material)
    {
        fixed (VkDescriptorSetLayout* pDescriptorSetLayout = &descriptorSetLayout0)
        {
            var allocInfo = new VkDescriptorSetAllocateInfo
            {
                sType = VkStructureType.DescriptorSetAllocateInfo,
                descriptorPool = descriptorPool,
                descriptorSetCount = 1,
                pSetLayouts = pDescriptorSetLayout,
            };

            VkDescriptorSet descriptorSet;
            deviceApi.vkAllocateDescriptorSets(&allocInfo, &descriptorSet).CheckResult();

            var imageInfos = stackalloc VkDescriptorImageInfo[4];

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

            var writeDescriptorSets = stackalloc VkWriteDescriptorSet[4];

            for (var i = 0; i < 4; i++)
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

            deviceApi.vkUpdateDescriptorSets(4, writeDescriptorSets, 0, null);

            primitive.DescriptorSet = descriptorSet;
        }
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

        Vma.vmaCreateImage(allocator, imageInfo, allocInfo, out var image, out _, null).CheckResult();

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

        deviceApi.vkCreateImageView(&viewInfo, null, out var imageView).CheckResult();

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

        deviceApi.vkCreateSampler(&samplerInfo, null, out var sampler).CheckResult();

        textureImages.Add(image);
        textureImageViews.Add(imageView);
        textureSamplers.Add(sampler);

        return new TextureHandle((uint)(textureImages.Count - 1));
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

        deviceApi.vkAllocateCommandBuffer(&allocInfo, out var commandBuffer).CheckResult();

        var beginInfo = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.CommandBufferBeginInfo,
            flags = VkCommandBufferUsageFlags.OneTimeSubmit
        };

        deviceApi.vkBeginCommandBuffer(commandBuffer, &beginInfo).CheckResult();

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
            image = depthImage,
            subresourceRange = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.Depth,
                baseMipLevel = 0,
                levelCount = 1,
                baseArrayLayer = 0,
                layerCount = 1
            }
        };

        deviceApi.vkCmdPipelineBarrier(
            commandBuffer,
            VkPipelineStageFlags.TopOfPipe,
            VkPipelineStageFlags.EarlyFragmentTests |
            VkPipelineStageFlags.LateFragmentTests,
            VkDependencyFlags.None,
            0, null,
            0, null,
            1, &barrier);

        deviceApi.vkEndCommandBuffer(commandBuffer).CheckResult();

        var submitInfo = new VkSubmitInfo
        {
            sType = VkStructureType.SubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &commandBuffer
        };

        deviceApi.vkQueueSubmit(graphicsQueue, submitInfo, VkFence.Null).CheckResult();
        deviceApi.vkQueueWaitIdle(graphicsQueue).CheckResult();

        deviceApi.vkFreeCommandBuffers(commandPool, 1, &commandBuffer);
    }

    private void CreateDescriptorSetLayout()
    {
        var set0Bindings = stackalloc VkDescriptorSetLayoutBinding[4];

        for (var i = 0; i < 4; i++)
        {
            set0Bindings[i] = new VkDescriptorSetLayoutBinding
            {
                binding = (uint)i,
                descriptorType = VkDescriptorType.CombinedImageSampler,
                descriptorCount = 1,
                stageFlags = VkShaderStageFlags.Fragment
            };
        }

        var set0Info = new VkDescriptorSetLayoutCreateInfo
        {
            sType = VkStructureType.DescriptorSetLayoutCreateInfo,
            bindingCount = 4,
            pBindings = set0Bindings
        };

        deviceApi.vkCreateDescriptorSetLayout(&set0Info, null, out descriptorSetLayout0).CheckResult();

        var set1Binding = new VkDescriptorSetLayoutBinding
        {
            binding = 0,
            descriptorType = VkDescriptorType.UniformBuffer,
            descriptorCount = 1,
            stageFlags = VkShaderStageFlags.Fragment
        };

        var set1Info = new VkDescriptorSetLayoutCreateInfo
        {
            sType = VkStructureType.DescriptorSetLayoutCreateInfo,
            bindingCount = 1,
            pBindings = &set1Binding
        };

        deviceApi.vkCreateDescriptorSetLayout(&set1Info, null, out descriptorSetLayout1).CheckResult();
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

        deviceApi.vkCreateDescriptorPool(&createInfo, null, out descriptorPool).CheckResult();
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
        fixed (VkDescriptorSet* pFrameDescriptorSet = &frameDescriptorSet)
        fixed (VkDescriptorSetLayout* pDescriptorSetLayout1 = &descriptorSetLayout1)
        {
            var allocInfo = new VkDescriptorSetAllocateInfo
            {
                sType = VkStructureType.DescriptorSetAllocateInfo,
                descriptorPool = descriptorPool,
                descriptorSetCount = 1,
                pSetLayouts = pDescriptorSetLayout1,
            };

            deviceApi.vkAllocateDescriptorSets(&allocInfo, pFrameDescriptorSet).CheckResult();

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

            deviceApi.vkUpdateDescriptorSets(1, &writeDescriptorSet, 0, null);
        }
    }

    private void UploadFrameConstants(FrameConstants constants)
    {
        void* mapped = null;
        Vma.vmaMapMemory(allocator, frameConstantAllocation, &mapped).CheckResult();
        *(FrameConstants*)mapped = constants;
        Vma.vmaUnmapMemory(allocator, frameConstantAllocation);
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
}

public struct Material
{
    public TextureHandle Albedo;
    public TextureHandle Normal;
    public TextureHandle MetallicRoughness;
    public TextureHandle Occlusion;
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