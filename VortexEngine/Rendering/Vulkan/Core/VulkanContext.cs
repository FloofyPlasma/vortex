using Vortice.Vulkan;

namespace VortexEngine.Rendering.Vulkan.Core;

using static Vortice.Vulkan.Vulkan;

internal sealed class VulkanContext : IDisposable
{
    private VmaAllocator _allocator;
    private VkDevice _device;
    private VkQueue _graphicsQueue;
    private VkInstance _instance;

    public VulkanContext(IVulkanSurfaceProvider surfaceProvider)
    {
        vkInitialize().CheckResult();

        CreateInstance(surfaceProvider);
        InstanceApi = GetApi(Instance);
        Surface = surfaceProvider.CreateSurface(Instance);
        SelectPhysicalDevice();
        FindQueueFamilies();
        CreateLogicalDevice();
        DeviceApi = GetApi(Instance, Device);
        DeviceApi.vkGetDeviceQueue(GraphicsQueueFamily, 0, out _graphicsQueue);
        CreateAllocator();
    }

    public VkInstance Instance => _instance;
    public VkPhysicalDevice PhysicalDevice { get; private set; }
    public VkDevice Device => _device;
    public VkInstanceApi InstanceApi { get; private set; }
    public VkDeviceApi DeviceApi { get; private set; }
    public VkQueue GraphicsQueue => _graphicsQueue;
    public uint GraphicsQueueFamily { get; private set; }
    public VmaAllocator Allocator => _allocator;
    public VkSurfaceKHR Surface { get; private set; }

    public unsafe void Dispose()
    {
        Vma.vmaDestroyAllocator(Allocator);
        DeviceApi.vkDestroyDevice(null);
    }

    private unsafe void CreateInstance(IVulkanSurfaceProvider surfaceProvider)
    {
        VkUtf8ReadOnlyString applicationName = "Vortex"u8;
        VkUtf8String engineName = "Vortex Engine"u8;

        var appInfo = new VkApplicationInfo
        {
            sType = VkStructureType.ApplicationInfo,
            pApplicationName = applicationName,
            applicationVersion = new VkVersion(1, 0, 0),
            pEngineName = engineName,
            engineVersion = new VkVersion(1, 0, 0),
            apiVersion = new VkVersion(1, 4, 0),
        };

        surfaceProvider.GetRequiredExtensions(out var extensions);
        using var vkExtensions = new VkStringArray(extensions);

        var createInfo = new VkInstanceCreateInfo
        {
            sType = VkStructureType.InstanceCreateInfo,
            pApplicationInfo = &appInfo,
            enabledExtensionCount = vkExtensions.Length,
            ppEnabledExtensionNames = vkExtensions
        };

        vkCreateInstance(&createInfo, out _instance).CheckResult();
    }

    private unsafe void FindQueueFamilies()
    {
        uint familyCount = 0;
        InstanceApi.vkGetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, &familyCount, null);

        var families = stackalloc VkQueueFamilyProperties[(int)familyCount];
        InstanceApi.vkGetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, &familyCount, families);

        for (uint i = 0; i < familyCount; i++)
        {
            if ((families[i].queueFlags & VkQueueFlags.Graphics) != 0)
            {
                GraphicsQueueFamily = i;
            }

            InstanceApi.vkGetPhysicalDeviceSurfaceSupportKHR(PhysicalDevice, i, Surface, out var presentSupport)
                .CheckResult();
        }
    }

    private unsafe void SelectPhysicalDevice()
    {
        uint physicalDeviceCount = 0;
        InstanceApi.vkEnumeratePhysicalDevices(&physicalDeviceCount, null).CheckResult();
        if (physicalDeviceCount == 0) throw new Exception("Failed to find physical device with Vulkan support");

        Span<VkPhysicalDevice> physicalDevices = stackalloc VkPhysicalDevice[(int)physicalDeviceCount];
        InstanceApi.vkEnumeratePhysicalDevices(physicalDevices).CheckResult();

        // TODO: Pick a device in a less shitty way :3
        PhysicalDevice = physicalDevices[0];
    }

    private unsafe void CreateLogicalDevice()
    {
        var queuePriority = 1.0f;
        var queueCreateInfos = stackalloc VkDeviceQueueCreateInfo[1];
        queueCreateInfos[0] = new VkDeviceQueueCreateInfo
        {
            sType = VkStructureType.DeviceQueueCreateInfo,
            queueFamilyIndex = GraphicsQueueFamily,
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
            pQueueCreateInfos = &queueCreateInfos[0],
            pEnabledFeatures = &features,
            enabledExtensionCount = (uint)extensions.Length,
            ppEnabledExtensionNames = extensionNames,
            pNext = &dynamicRenderingFeatures,
        };

        InstanceApi.vkCreateDevice(PhysicalDevice, &createInfo, null, out _device).CheckResult();
    }

    private void CreateAllocator()
    {
        var allocatorInfo = new VmaAllocatorCreateInfo
        {
            physicalDevice = PhysicalDevice,
            device = Device,
            instance = Instance
        };

        Vma.vmaCreateAllocator(in allocatorInfo, out _allocator).CheckResult();
    }
}