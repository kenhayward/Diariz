module.exports = {
  root: true,
  env: { browser: false, es6: true, node: true },
  parser: "@typescript-eslint/parser",
  parserOptions: { ecmaVersion: 2022, sourceType: "module" },
  ignorePatterns: [".eslintrc.js", "dist/**", "dist-test/**", "node_modules/**", "gulpfile.js"],
  overrides: [
    {
      files: ["package.json"],
      plugins: ["n8n-nodes-base"],
      extends: ["plugin:n8n-nodes-base/community"],
      rules: {
        "n8n-nodes-base/community-package-json-name-still-default": "off",
      },
    },
    {
      files: ["./credentials/**/*.ts"],
      plugins: ["n8n-nodes-base"],
      extends: ["plugin:n8n-nodes-base/credentials"],
      rules: {
        // Contradicts cred-class-field-documentation-url-not-http-url, which is the rule that applies to a
        // community node: built-in credentials use a camelCase docs slug, ours points at a real URL.
        "n8n-nodes-base/cred-class-field-documentation-url-miscased": "off",
      },
    },
    {
      files: ["./nodes/**/*.ts"],
      plugins: ["n8n-nodes-base"],
      extends: ["plugin:n8n-nodes-base/nodes"],
    },
  ],
};
