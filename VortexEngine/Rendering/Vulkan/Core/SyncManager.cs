using Vortice.Vulkan;

namespace VortexEngine.Rendering.Vulkan.Core;

internal sealed class SyncManager : IDisposable
{
    private const int MaxFramesInFlight = 2;
    private readonly VulkanContext ctx;

    private readonly VkSemaphore[] imageAvailableSemaphores;
    private readonly VkFence[] inFlightFences;
    private readonly VkSemaphore[] renderFinishedSemaphores;

    public SyncManager(VulkanContext ctx)
    {
        this.ctx = ctx;

        inFlightFences = new VkFence[MaxFramesInFlight];
        renderFinishedSemaphores = new VkSemaphore[MaxFramesInFlight];
        imageAvailableSemaphores = new VkSemaphore[MaxFramesInFlight];

        CreateSyncPrimitives();
    }

    public void Dispose()
    {
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            ctx.DeviceApi.vkDestroySemaphore(renderFinishedSemaphores[i]);
            ctx.DeviceApi.vkDestroySemaphore(imageAvailableSemaphores[i]);
            ctx.DeviceApi.vkDestroyFence(inFlightFences[i]);
        }
    }

    public VkSemaphore GetImageAvailableSemaphore(int frame)
    {
        return imageAvailableSemaphores[frame];
    }

    public VkSemaphore GetRenderFinishedSemaphore(int frame)
    {
        return renderFinishedSemaphores[frame];
    }

    public VkFence GetInFlightFence(int frame)
    {
        return inFlightFences[frame];
    }

    public unsafe void WaitForFrame(int frame)
    {
        var fence = inFlightFences[frame];
        ctx.DeviceApi.vkWaitForFences(1, &fence, true, ulong.MaxValue).CheckResult();
    }

    public unsafe void ResetFrameFence(int frame)
    {
        var fence = inFlightFences[frame];
        ctx.DeviceApi.vkResetFences(1, &fence).CheckResult();
    }

    private void CreateSyncPrimitives()
    {
        var semaphoreInfo = new VkSemaphoreCreateInfo
        {
            sType = VkStructureType.SemaphoreCreateInfo,
        };

        var fenceInfo = new VkFenceCreateInfo
        {
            sType = VkStructureType.FenceCreateInfo,
            flags = VkFenceCreateFlags.Signaled
        };

        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            unsafe
            {
                ctx.DeviceApi.vkCreateSemaphore(in semaphoreInfo, null, out imageAvailableSemaphores[i]).CheckResult();
                ctx.DeviceApi.vkCreateSemaphore(in semaphoreInfo, null, out renderFinishedSemaphores[i]).CheckResult();
                ctx.DeviceApi.vkCreateFence(in fenceInfo, null, out inFlightFences[i]).CheckResult();
            }
        }
    }
}