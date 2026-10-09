"""游戏崩溃分析：从游戏输出、崩溃报告和 JVM 错误日志中找出常见原因，给出中文说明。"""
import glob
import os
import re

MOD = "mod"  # 模组加载失败一类的问题，游戏可能正常退出（退出码 0）也需要提示
OTHER = "other"


def _read(path, limit=2 * 1024 * 1024):
    try:
        with open(path, "rb") as f:
            return f.read(limit).decode("utf-8", "replace").replace("\r\n", "\n")
    except OSError:
        return ""


def _newest(pattern, since):
    files = [p for p in glob.glob(pattern) if os.path.getmtime(p) >= since]
    return max(files, key=os.path.getmtime) if files else None


def _unique(items):
    seen, out = set(), []
    for item in items:
        if item not in seen:
            seen.add(item)
            out.append(item)
    return out


def _java_from_class_version(v):
    return int(float(v)) - 44


def _mod_dependency_reasons(text):
    reasons = []
    # Forge 1.13+ / NeoForge
    for mod_id, requester, wanted, actual in re.findall(
            r"Mod ID: '([^']+)', Requested by: '([^']+)', Expected range: '([^']+)', Actual version: '([^']+)'", text):
        if mod_id == "minecraft":
            reasons.append("模组「{}」不适用于当前游戏版本（需要 Minecraft {}）".format(requester, wanted))
        elif "MISSING" in actual.upper():
            reasons.append("模组「{}」缺少前置模组「{}」（需要版本 {}）".format(requester, mod_id, wanted))
        else:
            reasons.append("模组「{}」需要「{}」的版本 {}，当前为 {}".format(requester, mod_id, wanted, actual))
    # 1.12.2 及更早的 Forge
    for requester, deps in re.findall(r"Mod \S+ \(([^)]+)\) requires \[([^\]]+)\]", text):
        for dep in deps.split(","):
            reasons.append("模组「{}」缺少前置模组「{}」".format(requester, dep.split("@")[0].strip()))
    for requester, deps in re.findall(r"The mod \S+ \(([^)]+)\) requires mods \[([^\]]+)\]", text):
        for dep in deps.split(","):
            reasons.append("模组「{}」缺少前置模组「{}」".format(requester, dep.split("@")[0].strip()))
    # Fabric / Quilt
    for line in text.splitlines():
        if "which is missing" in line:
            mod = re.search(r"Mod '([^']+)'", line)
            dep = re.search(r"of (?:mod )?'?([^,']+?)'?(?: \(([^)]+)\))?, which is missing", line)
            if mod and dep:
                reasons.append("模组「{}」缺少前置模组「{}」".format(mod.group(1), dep.group(2) or dep.group(1)))
        elif "but only the wrong version is present" in line:
            mod = re.search(r"Mod '([^']+)'", line)
            dep = re.search(r"requires (.+?) of (?:mod )?'?([^,']+?)'?(?: \(([^)]+)\))?, but only", line)
            if mod and dep:
                target = dep.group(3) or dep.group(2)
                wanted = re.sub(r"^(?:any )?version ", "", dep.group(1))
                if target == "minecraft":
                    reasons.append("模组「{}」不适用于当前游戏版本（需要 Minecraft {}）".format(mod.group(1), wanted))
                else:
                    reasons.append("模组「{}」需要「{}」的 {} 版本".format(mod.group(1), target, wanted))
        elif "is incompatible with" in line and "Mod '" in line:
            mods = re.findall(r"'([^']+)'", line)
            if len(mods) >= 2:
                reasons.append("模组「{}」与「{}」不兼容，请删除其中一个".format(mods[0], mods[1]))
    return reasons


def _suspected_mods(report):
    """Forge / NeoForge 崩溃报告中的 Suspected Mods 段落。"""
    mods = []
    m = re.search(r"Suspected Mods?:(.*?)(?:\n\s*\n|\nStacktrace:)", report, re.S)
    if m and "NONE" not in m.group(1)[:20].upper():
        for line in m.group(1).splitlines():
            name = re.match(r"\s*(.+?) \(([^)]+)\)", line)
            if name:
                mods.append(name.group(1))
    return mods


def analyze(lines, game_dir, since, exit_code):
    """返回 {"reasons": [(类别, 文本)], "detail": 关键错误行, "files": [相关日志文件]}。"""
    report_path = _newest(os.path.join(game_dir, "crash-reports", "crash-*.txt"), since - 2)
    hs_err_path = _newest(os.path.join(game_dir, "hs_err_pid*.log"), since - 2)
    report = _read(report_path) if report_path else ""
    hs_err = _read(hs_err_path, 256 * 1024) if hs_err_path else ""
    output = "\n".join(lines)
    if len(lines) < 50:
        latest = os.path.join(game_dir, "logs", "latest.log")
        if os.path.isfile(latest) and os.path.getmtime(latest) >= since - 2:
            output += "\n" + _read(latest)
    text = "\n".join((report, hs_err, output))
    reasons = []

    def add(kind, message):
        reasons.append((kind, message))

    # ---------------------------------------------------------------- Java 版本
    m = re.search(r"class file version (\d+(?:\.\d+)?)\), this version of the Java Runtime only recognizes "
                  r"class file versions up to (\d+(?:\.\d+)?)", text)
    if m:
        add(OTHER, "游戏或模组需要 Java {} 及以上，当前使用的是 Java {}。请在设置或版本设置中更换 Java"
            .format(_java_from_class_version(m.group(1)), _java_from_class_version(m.group(2))))
    elif re.search(r"Unsupported major\.minor version (\d+)", text):
        version = re.search(r"Unsupported major\.minor version (\d+)", text).group(1)
        add(OTHER, "游戏或模组需要 Java {} 及以上，当前 Java 版本过低".format(_java_from_class_version(version)))
    if re.search(r"AppClassLoader cannot be cast to (?:class )?java\.net\.URLClassLoader", text):
        add(OTHER, "这个版本需要 Java 8，当前 Java 版本过高。请在版本设置中指定 Java 8")
    elif re.search(r"InaccessibleObjectException|module java\.base does not \"opens", text) and \
            re.search(r"forge|fml|launchwrapper", text, re.I):
        add(OTHER, "当前 Java 版本过高，与这个版本的 Forge 或模组不兼容。请在版本设置中指定较低版本的 Java")

    # ---------------------------------------------------------------- 内存
    if re.search(r"Could not reserve enough space for .*object heap|Invalid maximum heap size", text):
        add(OTHER, "无法分配设置的内存：可能使用了 32 位 Java，或最大内存设置过大")
    elif re.search(r"There is insufficient memory for the Java Runtime Environment|Native memory allocation", text):
        add(OTHER, "系统可用内存不足：请关闭其他程序，或适当调低最大内存")
    elif "java.lang.OutOfMemoryError" in text or "Out of Memory Error" in text:
        add(OTHER, "游戏内存不足：请在设置中调高最大内存（大型整合包建议 6 GB 以上）")

    # ---------------------------------------------------------------- 显卡
    if re.search(r"Pixel format not accelerated|GLFW error 6554[23]|WGL: The driver does not appear to support "
                 r"OpenGL|Couldn't set pixel format|No OpenGL context found|OpenGL \S+ is not supported", text):
        add(OTHER, "显卡驱动不支持 OpenGL 或驱动有问题：请更新显卡驱动；双显卡电脑请让 Java 使用独立显卡")
    elif hs_err and re.search(r"(atio6axx|atioglxx|nvoglv(?:64|32)|ig\w*icd(?:64|32)|igxelpicd64)\.dll", hs_err, re.I):
        add(OTHER, "显卡驱动崩溃：请更新显卡驱动；如果安装了光影，可以先关闭光影再试")

    # ---------------------------------------------------------------- 模组
    for reason in _unique(_mod_dependency_reasons(text)):
        add(MOD, reason)
    m = re.search(r"A potential solution has been determined.*?:\n((?:\s*- .+\n?)+)", text)
    if m and not any(kind == MOD for kind, _ in reasons):
        for line in m.group(1).strip().splitlines()[:6]:
            add(MOD, "Fabric 建议：" + line.strip(" \t-"))
    if re.search(r"[Dd]uplicate mods?|DuplicateModsFoundException|Found a duplicate mod|"
                 r"Mod ID .+ (?:is )?duplicated|duplicate mod ids", text):
        names = _unique(re.findall(r"Mod ID: '([^']+)' from mod files?:", text)
                        + re.findall(r"Duplicate mods? .*?'([^']+)'", text))
        add(MOD, "有重复安装的模组{}，请在 mods 文件夹中只保留一个版本"
            .format("：" + "、".join(names[:5]) if names else ""))
    for mod in _unique(re.findall(r"Mixin apply for mod (\S+) failed", text)
                       + re.findall(r"from mod (\S+?)\] failed injection check", text))[:5]:
        add(MOD, "模组「{}」注入失败，可能与当前游戏版本或其他模组不兼容".format(mod))
    if re.search(r"net\.optifine|optifine\.", text, re.I) and re.search(r"NoSuchMethodError|NoSuchFieldError", text):
        add(MOD, "OptiFine 与当前 Forge 或其他模组不兼容：请更换 OptiFine / Forge 版本，或移除 OptiFine")
    for mod in _suspected_mods(report)[:5]:
        add(MOD, "崩溃报告指出可能与模组「{}」有关，可以尝试更新或移除它".format(mod))

    # ---------------------------------------------------------------- 其他
    if re.search(r"zip END header not found|Invalid or corrupt jarfile|error in opening zip file|"
                 r"ZipException: (?:invalid|zip file is empty)", text):
        add(OTHER, "有文件已损坏：请重新下载出问题的模组，或在启动时让启动器补全游戏文件")
    if re.search(r"UnsatisfiedLinkError|Failed to locate library: \S+\.dll", text):
        add(OTHER, "游戏本地库加载失败：可能是游戏路径包含特殊字符，或 Java 与系统架构不匹配（32 / 64 位）")
    if "Manually triggered debug crash" in text:
        add(OTHER, "这是按住 F3 + C 手动触发的崩溃，不是错误")
    if "java.lang.StackOverflowError" in text and not reasons:
        add(OTHER, "游戏发生了栈溢出，通常由模组之间的冲突引起；也可以在额外 JVM 参数中加入 -Xss4m 再试")
    if not reasons:
        for cls in _unique(re.findall(r"(?:NoClassDefFoundError|ClassNotFoundException): ([\w.$/]+)", text))[:3]:
            add(MOD if "minecraft" not in cls else OTHER,
                "找不到类 {}：可能缺少前置模组，或模组与当前版本不匹配".format(cls.replace("/", ".")))
    if exit_code in (-1073741819, 3221225477) and not reasons:
        add(OTHER, "游戏进程发生内存访问冲突，通常是显卡驱动或光影导致：请更新显卡驱动并关闭光影")

    detail = ""
    if report:
        m = re.search(r"Description: (.+)\n+(.+)", report)
        if m:
            detail = "{}：{}".format(m.group(1).strip(), m.group(2).strip())
    if not detail:
        m = re.search(r"^(?:Exception in thread \"[^\"]+\" )?((?:[\w$]+\.)+[\w$]*(?:Exception|Error)\b.*)$", output, re.M)
        if m:
            detail = m.group(1).strip()
    return {
        "reasons": reasons,
        "detail": detail[:300],
        "files": [p for p in (report_path, hs_err_path) if p],
    }
