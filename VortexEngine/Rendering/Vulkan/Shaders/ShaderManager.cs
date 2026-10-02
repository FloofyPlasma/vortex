using System.Runtime.InteropServices;
using VortexEngine.Rendering.Vulkan.Core;
using Vortice.ShaderCompiler;
using Vortice.Vulkan;

namespace VortexEngine.Rendering.Vulkan.Shaders;

internal sealed class ShaderManager : IDisposable
{
    private readonly Dictionary<string, VkPipeline> computePipelines = [];
    private readonly VulkanContext ctx;
    private readonly Dictionary<string, VkDescriptorSetLayout> descriptorLayouts = [];
    private readonly Dictionary<string, VkPipeline> graphicsPipelines = [];
    private readonly Dictionary<string, VkPipelineLayout> pipelineLayouts = [];

    private readonly Dictionary<string, VkShaderModule> shaderModules = [];
    private readonly SwapchainManager swapchain;

    public ShaderManager(VulkanContext context, SwapchainManager swapchainManager)
    {
        ctx = context;
        swapchain = swapchainManager;

        RegisterGraphicsShader(ShaderDefinitions.PBRMesh);
        RegisterComputeShader(ShaderDefinitions.EquirectangularToCubemap);
        RegisterComputeShader(ShaderDefinitions.BrdfLut);
    }

    public unsafe void Dispose()
    {
        foreach (var module in shaderModules.Values)
        {
            ctx.DeviceApi.vkDestroyShaderModule(module, null);
        }

        foreach (var pipeline in graphicsPipelines.Values)
        {
            ctx.DeviceApi.vkDestroyPipeline(pipeline, null);
        }

        foreach (var pipeline in computePipelines.Values)
        {
            ctx.DeviceApi.vkDestroyPipeline(pipeline, null);
        }

        foreach (var layout in pipelineLayouts.Values)
        {
            ctx.DeviceApi.vkDestroyPipelineLayout(layout, null);
        }

        foreach (var layout in descriptorLayouts.Values)
        {
            ctx.DeviceApi.vkDestroyDescriptorSetLayout(layout, null);
        }
    }

    public void RegisterGraphicsShader(ShaderDefinition def)
    {
        var vertexCode = ShaderCompiler.LoadAndCompileGlsl(def.VertexShaderPath, ShaderKind.VertexShader, def.Name);
        var vertexShader = ShaderCompiler.CreateShaderModule(ctx.DeviceApi, vertexCode);

        var fragmentcode =
            ShaderCompiler.LoadAndCompileGlsl(def.FragmentShaderPath, ShaderKind.FragmentShader, def.Name);
        var fragmentShader = ShaderCompiler.CreateShaderModule(ctx.DeviceApi, fragmentcode);

        shaderModules[$"{def.Name}:vert"] = vertexShader;
        shaderModules[$"{def.Name}:frag"] = fragmentShader;

        foreach (var setDef in def.DescriptorSets)
        {
            var layout = CreateDescriptorSetLayout(setDef.Bindings);
            descriptorLayouts[$"{def.Name}:set{setDef.Set}"] = layout;
        }

        var setLayouts = def.DescriptorSets
            .OrderBy(s => s.Set)
            .Select(s => descriptorLayouts[$"{def.Name}:set{s.Set}"])
            .ToArray();

        var pipelineLayout = CreatePipelineLayout(setLayouts, def.PushConstants);
        pipelineLayouts[def.Name] = pipelineLayout;

        var pipeline = CreateGraphicsPipeline(def, vertexShader, fragmentShader, pipelineLayout);
        graphicsPipelines[def.Name] = pipeline;
    }

    public void RegisterComputeShader(ComputeShaderDefinition def)
    {
        var computeCode = ShaderCompiler.LoadAndCompileGlsl(def.ComputeShaderPath, ShaderKind.ComputeShader, def.Name);
        var computeShader = ShaderCompiler.CreateShaderModule(ctx.DeviceApi, computeCode);

        shaderModules[def.Name] = computeShader;

        foreach (var setDef in def.DescriptorSets)
        {
            var layout = CreateDescriptorSetLayout(setDef.Bindings);
            descriptorLayouts[$"{def.Name}:set{setDef.Set}"] = layout;
        }

        var setLayouts = def.DescriptorSets
            .OrderBy(s => s.Set)
            .Select(s => descriptorLayouts[$"{def.Name}:set{s.Set}"])
            .ToArray();

        var pipelineLayout = CreatePipelineLayout(setLayouts, def.PushConstants);
        pipelineLayouts[def.Name] = pipelineLayout;

        var pipeline = CreateComputePipeline(computeShader, pipelineLayout);
        computePipelines[def.Name] = pipeline;
    }

    public VkPipeline GetGraphicsPipeline(string name) => graphicsPipelines[name];
    public VkPipeline GetComputePipeline(string name) => computePipelines[name];
    public VkPipelineLayout GetPipelineLayout(string name) => pipelineLayouts[name];

    public VkDescriptorSetLayout GetDescriptorSetLayout(string shaderName, uint setIndex)
        => descriptorLayouts[$"{shaderName}:set{setIndex}"];

    private VkDescriptorSetLayout CreateDescriptorSetLayout(List<DescriptorBinding> bindings)
    {
        var vkBindings = bindings.Select(b => new VkDescriptorSetLayoutBinding
        {
            binding = b.Binding,
            descriptorType = b.Type,
            descriptorCount = 1,
            stageFlags = b.Stages
        }).ToArray();

        var layoutInfo = new VkDescriptorSetLayoutCreateInfo
        {
            sType = VkStructureType.DescriptorSetLayoutCreateInfo,
            bindingCount = (uint)vkBindings.Length
        };

        unsafe
        {
            fixed (VkDescriptorSetLayoutBinding* pBindings = vkBindings)
            {
                layoutInfo.pBindings = pBindings;
                ctx.DeviceApi.vkCreateDescriptorSetLayout(&layoutInfo, null, out var layout).CheckResult();
                return layout;
            }
        }
    }

    private VkPipelineLayout CreatePipelineLayout(VkDescriptorSetLayout[] setLayouts,
        PushConstantDefinition? pushConstants)
    {
        fixed (VkDescriptorSetLayout* pSetLayouts = setLayouts)
        {
            var pushConstantRange = pushConstants != null
                ? new VkPushConstantRange
                {
                    stageFlags = pushConstants.Stages,
                    offset = pushConstants.Offset,
                    size = pushConstants.Size
                }
                : default;

            unsafe
            {
                var pipelineLayoutInfo = new VkPipelineLayoutCreateInfo
                {
                    sType = VkStructureType.PipelineLayoutCreateInfo,
                    setLayoutCount = (uint)setLayouts.Length,
                    pSetLayouts = pSetLayouts,
                    pushConstantRangeCount = pushConstants != null ? 1u : 0u,
                    pPushConstantRanges = pushConstants != null ? &pushConstantRange : null,
                };

                ctx.DeviceApi.vkCreatePipelineLayout(&pipelineLayoutInfo, null, out var layout).CheckResult();
                return layout;
            }
        }
    }

    private unsafe VkPipeline CreateGraphicsPipeline(ShaderDefinition def, VkShaderModule vertexShader,
        VkShaderModule fragmentShader, VkPipelineLayout pipelineLayout)
    {
        VkUtf8ReadOnlyString vertexEntryPoint = "main"u8;
        VkUtf8ReadOnlyString fragmentEntryPoint = "main"u8;

        var vertexShaderStage = new VkPipelineShaderStageCreateInfo
        {
            sType = VkStructureType.PipelineShaderStageCreateInfo,
            stage = VkShaderStageFlags.Vertex,
            module = vertexShader,
            pName = vertexEntryPoint
        };

        var fragmentShaderStage = new VkPipelineShaderStageCreateInfo
        {
            sType = VkStructureType.PipelineShaderStageCreateInfo,
            stage = VkShaderStageFlags.Fragment,
            module = fragmentShader,
            pName = fragmentEntryPoint
        };

        var shaderStages = stackalloc VkPipelineShaderStageCreateInfo[2];
        shaderStages[0] = vertexShaderStage;
        shaderStages[1] = fragmentShaderStage;

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
            y = swapchain.Extent.height,
            width = (float)swapchain.Extent.width,
            height = (float)-swapchain.Extent.height,
            minDepth = 0.0f,
            maxDepth = 1.0f,
        };

        var scissor = new VkRect2D
        {
            offset = new VkOffset2D(0, 0),
            extent = swapchain.Extent
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

        var colorFormat = swapchain.ImageFormat;

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
            pStages = shaderStages,
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

        var pipelines = stackalloc VkPipeline[1];
        ctx.DeviceApi.vkCreateGraphicsPipelines(VkPipelineCache.Null, 1, &pipelineInfo, pipelines).CheckResult();
        return pipelines[0];
    }

    private unsafe VkPipeline CreateComputePipeline(VkShaderModule computeShader, VkPipelineLayout pipelineLayout)
    {
        VkUtf8ReadOnlyString entryPoint = "main"u8;

        var shaderStageInfo = new VkPipelineShaderStageCreateInfo
        {
            sType = VkStructureType.PipelineShaderStageCreateInfo,
            stage = VkShaderStageFlags.Compute,
            module = computeShader,
            pName = entryPoint
        };

        var pipelineInfo = new VkComputePipelineCreateInfo
        {
            sType = VkStructureType.ComputePipelineCreateInfo,
            layout = pipelineLayout,
            stage = shaderStageInfo,
        };

        ctx.DeviceApi.vkCreateComputePipeline(VkPipelineCache.Null, pipelineInfo, out var pipeline).CheckResult();
        return pipeline;
    }
}
