require 'xcodeproj'
require 'fileutils'
require 'pathname'

root = File.expand_path(__dir__)
output = File.join(root, '.build')
FileUtils.mkdir_p(output)
project = Xcodeproj::Project.new(File.join(output, 'SolarStoreKit.xcodeproj'))
project.root_object.attributes['LastUpgradeCheck'] = '2700'
host = project.new_target(:application, 'SolarStoreKitHost', :ios, '18.0', nil, :swift)
tests = project.new_target(:unit_test_bundle, 'SolarStoreKitTests', :ios, '18.0', nil, :swift)
tests.add_dependency(host)
project.root_object.attributes['TargetAttributes'] = {
  host.uuid => { 'CreatedOnToolsVersion' => '27.0' },
  tests.uuid => { 'CreatedOnToolsVersion' => '27.0', 'TestTargetID' => host.uuid }
}
[host, tests].each do |target|
  target.build_configurations.each do |config|
    settings = config.build_settings
    settings['PRODUCT_BUNDLE_IDENTIFIER'] = 'com.dshapar.solar.localTests.' + target.name
    settings['SWIFT_VERSION'] = '5.0'
    settings['GENERATE_INFOPLIST_FILE'] = 'YES'
    settings['CODE_SIGNING_ALLOWED'] = 'NO'
    settings['TARGETED_DEVICE_FAMILY'] = '1,2'
    settings['SUPPORTED_PLATFORMS'] = 'iphonesimulator'
    settings['SWIFT_STRICT_CONCURRENCY'] = 'minimal'
    settings['LD_RUNPATH_SEARCH_PATHS'] = ['$(inherited)', '@executable_path/Frameworks', '@loader_path/Frameworks']
  end
end
host.build_configurations.each do |config|
  config.build_settings['INFOPLIST_KEY_UILaunchScreen_Generation'] = 'YES'
  config.build_settings['INFOPLIST_KEY_UIApplicationSceneManifest_Generation'] = 'YES'
end
tests.build_configurations.each do |config|
  config.build_settings['TEST_HOST'] = '$(BUILT_PRODUCTS_DIR)/SolarStoreKitHost.app/SolarStoreKitHost'
  config.build_settings['BUNDLE_LOADER'] = '$(TEST_HOST)'
  config.build_settings['ENABLE_TESTING_SEARCH_PATHS'] = 'YES'
end
# Xcode file references are relative to the generated project directory.
relative = ->(path) { Pathname.new(File.expand_path(path, root)).relative_path_from(Pathname.new(output)).to_s }
host.add_file_references([project.main_group.new_file(relative.call('SolarStoreKitHost/AppDelegate.swift'))])
tests.add_file_references([
  project.main_group.new_file(relative.call('SolarStoreKitTests/SolarStoreKitTests.swift')),
  project.main_group.new_file(relative.call('../ios/SolarSubscriptionStore.swift'))
])
host.resources_build_phase.add_file_reference(project.main_group.new_file(relative.call('SolarStoreKitTests/SolarLocal.storekit')))
tests.resources_build_phase.add_file_reference(project.main_group.new_file(relative.call('SolarStoreKitTests/SolarLocal.storekit')))
project.save
scheme = Xcodeproj::XCScheme.new
scheme.configure_with_targets(host, tests)
scheme.test_action.should_use_launch_scheme_args_env = false
scheme.test_action.xml_element.attributes['parallelizable'] = 'NO'
scheme.test_action.xml_element.attributes['systemAttachmentLifetime'] = 'keepNever'
scheme.test_action.xml_element.attributes['userAttachmentLifetime'] = 'keepNever'
scheme.launch_action.xml_element.add_element('StoreKitConfigurationFileReference', {
  'identifier' => '../../../SolarStoreKitTests/SolarLocal.storekit'
})
scheme.save_as(project.path, 'SolarStoreKit', true)
