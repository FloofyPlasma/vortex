using Vortice.Vulkan;
using Vortice.ShaderCompiler;

namespace VortexEngine.Rendering.Vulkan;

internal static class ShaderCompiler
{
    public static unsafe VkShaderModule CreateShaderModule(VkDeviceApi deviceApi, byte[] spirvCode,
        string? label = null)
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

    public static byte[] CompileGlslToSpirv(string glslSource, ShaderKind shaderKind, string? label = null)
    {
        using var compiler = new Compiler();
        var options = new CompilerOptions();
        options.SourceLanguage = SourceLanguage.GLSL;
        options.ShaderStage = shaderKind;

        var result = compiler.Compile(glslSource, label ?? "shader", options);
        return result.Status != CompilationStatus.Success ? throw new InvalidOperationException($"Shader compilation failed ({label}): {result.ErrorMessage}") : result.Bytecode;
    }

    public static byte[] LoadAndCompileGlsl(string filePath, ShaderKind shaderKind)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Shader file not found: {filePath}");

        var glslSource = File.ReadAllText(filePath);
        return CompileGlslToSpirv(glslSource, shaderKind, filePath);
    }
}