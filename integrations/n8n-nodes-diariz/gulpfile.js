const { src, dest } = require("gulp");

/** Copies node icons into dist, which tsc does not handle. The n8n starter convention. */
function buildIcons() {
  return src("nodes/**/*.{png,svg}", { encoding: false }).pipe(dest("dist/nodes"));
}

exports["build:icons"] = buildIcons;
