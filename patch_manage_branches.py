
file_path = "C:/Users/yaser/source/repos/Pos.Solution/POS.Web/Components/Pages/ManageBranches.razor"
with open(file_path, "r") as f:
    lines = f.readlines()

# 1. Add injection
for i, line in enumerate(lines):
    if "@inject IBusinessTypeService BusinessTypeService" in line:
        lines.insert(i + 1, "@inject IServiceModeService ServiceModeService\n")
        break

# 2. Add serviceModes field
for i, line in enumerate(lines):
    if "private List<BusinessTypeDto>? businessTypes;" in line:
        lines.insert(i + 1, "    private List<ServiceModeDto>? serviceModes;\n")
        break

# 3. Update LoadBranches
for i, line in enumerate(lines):
    if "businessTypes = await BusinessTypeService.GetBusinessTypesAsync();" in line:
        lines.insert(i + 1, "            serviceModes = await ServiceModeService.GetServiceModesAsync();\n")
        break

# 4. Update modal form
# Looking for </label>\n *<InputSelect id="branchBusinessType" ...
# and adding service mode dropdown after it
for i, line in enumerate(lines):
    if "id=\"branchBusinessType\"" in line:
        # Find closing </div>
        for j in range(i, i + 10):
            if "</div>" in lines[j]:
                lines.insert(j + 1, """                    <div class="mb-3">
                        <label for="branchServiceMode" class="form-label">Service Mode</label>
                        <InputSelect id="branchServiceMode" class="form-select" @bind-Value="currentBranch.ServiceModeId" disabled="@(serviceModes == null)">
                            <option value="">Unassigned</option>
                            @foreach (var mode in serviceModes ?? [])
                            {
                                <option value="@mode.ServiceModeId">@mode.Type</option>
                            }
                        </InputSelect>
                    </div>\n""")
                break
        break

with open(file_path, "w") as f:
    f.writelines(lines)
