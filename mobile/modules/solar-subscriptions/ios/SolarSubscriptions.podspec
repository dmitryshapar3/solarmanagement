Pod::Spec.new do |s|
  s.name = 'SolarSubscriptions'
  s.version = '1.0.0'
  s.summary = 'DeyeSolar Apple StoreKit 2 subscriptions'
  s.description = 'Local Expo module for verified auto-renewable Apple subscriptions.'
  s.author = 'DeyeSolar'
  s.homepage = 'https://github.com/dmitryshapar3/solarmanagement'
  s.license = { :type => 'Proprietary' }
  s.source = { :git => 'https://github.com/dmitryshapar3/solarmanagement.git' }
  s.platform = :ios, '16.4'
  s.swift_version = '5.9'
  s.static_framework = true
  s.dependency 'ExpoModulesCore'
  s.frameworks = 'StoreKit', 'UIKit'
  s.source_files = '**/*.swift'
  s.pod_target_xcconfig = { 'DEFINES_MODULE' => 'YES' }
end
