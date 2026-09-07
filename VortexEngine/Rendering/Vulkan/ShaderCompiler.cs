using Vortice.Vulkan;

namespace VortexEngine.Rendering.Vulkan;

internal static class ShaderCompiler
{
    public static unsafe VkShaderModule CreateShaderModule(VkDeviceApi deviceApi, byte[] spirvCode, string? label = null)
    {
        if (spirvCode.Length == 0) throw new InvalidOperationException("SPIR-V code is empty");

        if (spirvCode.Length % 4 != 0)
            throw new InvalidOperationException("SPIR-V code size must be a multiple of 4 bytes");

        var createInfo = new VkShaderModuleCreateInfo
        {
            sType = VkStructureType.ShaderModuleCreateInfo,
            codeSize = (uint)spirvCode.Length,
        };

        fixed (byte* codePtr = spirvCode)
        {
            createInfo.pCode = (uint*)codePtr;
            deviceApi.vkCreateShaderModule(&createInfo, null, out var shaderModule).CheckResult();

            return shaderModule;
        }
    }

    public static byte[] LoadSpirV(string path)
    {
        return !File.Exists(path) ? throw new FileNotFoundException($"SPIR-V file not found: {path}") : File.ReadAllBytes(path);
    }
}