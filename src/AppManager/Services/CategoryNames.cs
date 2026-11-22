using AppManager.Core.Models;

namespace AppManager.Services;

/// <summary>Chinese display labels for FolderCategory — single source of truth.</summary>
public static class CategoryNames
{
    public static string Of(FolderCategory category) => category switch
    {
        FolderCategory.UserHomeDot => "家目录 .xxx 文件夹",
        FolderCategory.UserHome => "家目录其他",
        FolderCategory.Documents => "文档",
        FolderCategory.SavedGames => "保存的游戏",
        FolderCategory.RoamingAppData => @"AppData\Roaming",
        FolderCategory.LocalAppData => @"AppData\Local",
        FolderCategory.LocalLowAppData => @"AppData\LocalLow",
        FolderCategory.ProgramData => "ProgramData",
        FolderCategory.PackageData => "应用包数据 (MSIX)",
        FolderCategory.LocalPrograms => @"用户级程序 (Local\Programs)",
        FolderCategory.InstallDir => "安装目录",
        FolderCategory.Custom => "自定义目录",
        _ => category.ToString(),
    };

    public static IReadOnlyList<FolderCategory> All { get; } =
        Enum.GetValues<FolderCategory>();
}

/// <summary>Chinese display labels for HarnessKind — single source of truth.</summary>
public static class HarnessKindNames
{
    public static string Of(HarnessKind kind) => kind switch
    {
        HarnessKind.Conversations => "会话与记忆",
        HarnessKind.Artifacts => "附件与生成文件",
        HarnessKind.Snapshots => "文件快照与备份",
        HarnessKind.Cache => "缓存",
        HarnessKind.Logs => "日志",
        HarnessKind.Extensions => "插件与扩展",
        HarnessKind.State => "配置与其他状态",
        HarnessKind.OldVersions => "旧版本与安装包",
        HarnessKind.Program => "程序本体",
        _ => kind.ToString(),
    };
}
