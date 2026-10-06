const { withInfoPlist, withXcodeProject, withDangerousMod } = require('@expo/config-plugins');
const fs = require('fs');
const path = require('path');
module.exports = function withSmartSolarNative(config) {
  config = withInfoPlist(config, mod => {
    mod.modResults.CFBundleShortVersionString = '$(MARKETING_VERSION)';
    mod.modResults.CFBundleVersion = '$(CURRENT_PROJECT_VERSION)';
    delete mod.modResults.NSLocationAlwaysUsageDescription;
    delete mod.modResults.NSLocationAlwaysAndWhenInUseUsageDescription;
    delete mod.modResults.NSMotionUsageDescription;
    return mod;
  });
  config = withXcodeProject(config, mod => {
    const entries = mod.modResults.pbxXCBuildConfigurationSection();
    for (const value of Object.values(entries)) {
      if (value && typeof value === 'object' && value.buildSettings?.PRODUCT_BUNDLE_IDENTIFIER === 'com.dshapar.solar') value.buildSettings.PRODUCT_NAME = 'DeyeSolar';
    }
    return mod;
  });
  return withDangerousMod(config, ['ios', async mod => {
    const file = path.join(mod.modRequest.platformProjectRoot, 'Podfile.properties.json');
    if (fs.existsSync(file)) {
      const props = JSON.parse(fs.readFileSync(file, 'utf8'));
      if (props['expo.inlineModules.xcodeProjectTargets']) {
        const targets = JSON.parse(props['expo.inlineModules.xcodeProjectTargets']);
        targets.mainTarget = 'DeyeSolar'; props['expo.inlineModules.xcodeProjectTargets'] = JSON.stringify(targets);
        fs.writeFileSync(file, JSON.stringify(props, null, 2) + '\n');
      }
    }
    return mod;
  }]);
};
