// 把指定 tag 的 GitHub Release 推到 Discord。
// 需要环境变量 DISCORD_WEBHOOK、RELEASE_TAG；GITHUB_REPOSITORY 可选。
const webhook = process.env.DISCORD_WEBHOOK;
if (!webhook) {
  console.log("未设置 DISCORD_WEBHOOK，跳过通知");
  process.exit(0);
}

const repo = process.env.GITHUB_REPOSITORY || "JinRuiMaoMao/MaoX-Launcher";
const tag = (process.env.RELEASE_TAG || process.argv[2] || "").trim();
if (!tag) {
  console.error("缺少 RELEASE_TAG");
  process.exit(1);
}

const api = await fetch(`https://api.github.com/repos/${repo}/releases/tags/${encodeURIComponent(tag)}`, {
  headers: {
    Accept: "application/vnd.github+json",
    "User-Agent": "MaoX-Launcher",
    "X-GitHub-Api-Version": "2022-11-28",
  },
});
if (!api.ok)
  throw new Error(`GitHub ${api.status}: ${await api.text()}`);
const rel = await api.json();

let raw = (rel.body || "").replace(/\r\n/g, "\n").trim();
const cut = raw.indexOf("\n---\n");
if (cut >= 0)
  raw = raw.slice(0, cut).trim();
const split = raw.match(/\n##\s*English\s*\n/i);
const zh = (split ? raw.slice(0, split.index) : raw).replace(/^##\s*中文\s*\n/i, "").trim();
const en = split ? raw.slice(split.index + split[0].length).trim() : "";
let body = zh && en
  ? `**中文**\n${zh}\n\n**English**\n${en}`
  : (zh || en || "新版本已发布。 / A new version is out.");
if (body.length > 4000)
  body = body.slice(0, 4000) + "…";

const res = await fetch(webhook, {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({
    username: "MaoX Launcher",
    embeds: [{
      title: rel.name || `MaoX Launcher ${rel.tag_name}`,
      url: rel.html_url,
      description: body,
      color: 0x00D9FF,
      fields: [
        { name: "版本 / Version", value: rel.tag_name, inline: true },
        { name: "下载 / Download", value: `[打开 Release / Open Release](${rel.html_url})`, inline: true },
      ],
    }],
  }),
});
if (!res.ok)
  throw new Error(`Discord ${res.status}: ${await res.text()}`);
console.log(`已推送到 Discord：${rel.tag_name}`);
