const path = require("path");
const { getDefaultConfig } = require("expo/metro-config");
const config = getDefaultConfig(__dirname);
// Web and mobile compile the same checked-in translation catalogs.
config.watchFolders = [...(config.watchFolders || []), path.resolve(__dirname, "../i18n")];
module.exports = config;
