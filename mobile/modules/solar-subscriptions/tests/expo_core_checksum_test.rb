require 'cocoapods'
gem 'minitest', '5.25.5'
require 'minitest/autorun'
require 'tmpdir'
# The saved-spec fixtures exercise Expo's real sandbox wrapper without autolinking
# or framework downloads. This child-process setting does not change pod install.
ENV['EXPO_USE_PRECOMPILED_MODULES'] = '0'
require_relative '../../../node_modules/expo-modules-autolinking/scripts/ios/cocoapods/sandbox'
require_relative '../../../ios/expo_core_checksum'

class ExpoCoreChecksumTest < Minitest::Test
  EXPECTED_TESTS = 16
  @completed_results = []

  class << self
    attr_reader :completed_results
  end

  def setup
    @directory = Dir.mktmpdir('solar-pod-checksum-')
    @root = File.join(@directory, 'mobile')
    @sandbox = Pod::Sandbox.new(File.join(@root, 'ios', 'Pods'))
  end

  def teardown
    FileUtils.remove_entry(@directory)
  end

  def after_teardown
    super
    self.class.completed_results << Minitest::Result.from(self)
  end

  def payload(root = @root, flavor: 'debug')
    archive = "#{root}/node_modules/expo-modules-core/prebuilds/output/#{flavor}/xcframeworks/ExpoModulesCore.tar.gz"
    {
      'name' => 'ExpoModulesCore', 'version' => '57.0.20',
      'platforms' => { 'ios' => '16.4', 'osx' => '13.4', 'tvos' => '16.4' },
      'source' => { 'http' => URI::File.build(path: URI::DEFAULT_PARSER.escape(archive)).to_s, 'flatten' => false },
      'vendored_frameworks' => ['ExpoModulesCore.xcframework'],
      'pod_target_xcconfig' => { 'OTHER_SWIFT_FLAGS' => '$(inherited) -DRCT_NEW_ARCH_ENABLED' },
      'prepare_command' => "if [ ! -d \"ExpoModulesCore.xcframework\" ]; then\n  TARBALL=\"#{archive}\"\n  tar xzf \"$TARBALL\"\nfi"
    }
  end

  def checksum(attributes, root = @root)
    SolarExpoCoreChecksum.checksum(JSON.generate(attributes), mobile_root: root)
  end

  def store(attributes, sandbox = @sandbox)
    FileUtils.mkdir_p(sandbox.specifications_root)
    spec = Pod::Specification.from_json(JSON.generate(attributes))
    sandbox.store_podspec(spec.name, spec, true, true)
  end

  def installer(spec, sandbox = @sandbox, lockfile: nil)
    podfile = Pod::Podfile.new
    dependency_cache = Struct.new(:podfile_dependencies).new([])
    analysis = Struct.new(:podfile_dependency_cache, :specifications, :specs_by_source).new(dependency_cache, [spec], {})
    instance = Pod::Installer.new(sandbox, podfile, lockfile)
    instance.instance_variable_set(:@analysis_result, analysis)
    instance
  end

  def test_two_checkouts_have_the_same_checksum
    other = '/Users/operator/Projects/SolarManagement/mobile'
    assert_equal checksum(payload), checksum(payload(other), other)
  end

  def test_spaces_and_unicode_checkouts_have_the_same_checksum
    other = '/Users/operator/Solar projects/Солнечная ☀️/mobile'
    assert_equal checksum(payload), checksum(payload(other), other)
  end

  def test_actual_saved_download_and_preparation_paths_are_unchanged
    original = payload
    spec = store(original)
    assert_equal checksum(original), spec.checksum
    assert_equal original['source'], spec.attributes_hash['source']
    assert_equal original['prepare_command'], spec.attributes_hash['prepare_command']
    saved = JSON.parse(File.read(spec.defined_in_file))
    assert_equal original['source'], saved['source']
    assert_equal original['prepare_command'], saved['prepare_command']
  end

  def test_new_sandbox_reload_retains_the_canonical_checksum
    first = store(payload)
    reloaded = Pod::Sandbox.new(@sandbox.root).specification('ExpoModulesCore')
    refute_same first, reloaded
    assert_equal first.checksum, reloaded.checksum
    assert_equal first.attributes_hash['source'], reloaded.attributes_hash['source']
  end

  def test_reapplication_passes_the_actual_deployment_lock_guard
    first = installer(store(payload))
    lockfile = first.send(:generate_lockfile)
    lock_path = File.join(@root, 'ios', 'Podfile.lock')
    lockfile.write_to_disk(Pathname.new(lock_path))
    next_sandbox = Pod::Sandbox.new(@sandbox.root)
    next_spec = next_sandbox.specification('ExpoModulesCore')
    next_install = installer(next_spec, next_sandbox, lockfile: Pod::Lockfile.from_file(Pathname.new(lock_path)))
    assert_nil next_install.send(:verify_no_lockfile_changes!)
    assert_equal lockfile.to_hash, next_install.send(:generate_lockfile).to_hash
  end

  def test_another_checkout_passes_the_actual_deployment_lock_guard
    first_lock = installer(store(payload)).send(:generate_lockfile)
    other = File.join(@directory, 'second checkout ☀️', 'mobile')
    next_sandbox = Pod::Sandbox.new(File.join(other, 'ios', 'Pods'))
    next_install = installer(store(payload(other), next_sandbox), next_sandbox, lockfile: first_lock)
    assert_nil next_install.send(:verify_no_lockfile_changes!)
    assert_equal first_lock.to_hash, next_install.send(:generate_lockfile).to_hash
  end

  def test_version_changes_are_detected
    changed = payload.merge('version' => '57.0.21')
    refute_equal checksum(payload), checksum(changed)
  end

  def test_build_setting_changes_are_detected
    changed = payload
    changed['pod_target_xcconfig']['OTHER_SWIFT_FLAGS'] += ' -DCHANGED'
    refute_equal checksum(payload), checksum(changed)
  end

  def test_archive_flavor_changes_are_detected
    refute_equal checksum(payload), checksum(payload(@root, flavor: 'release'))
  end

  def test_vendored_framework_changes_are_detected
    changed = payload.merge('vendored_frameworks' => ['Changed.xcframework'])
    refute_equal checksum(payload), checksum(changed)
  end

  def test_script_content_changes_are_detected
    changed = payload
    changed['prepare_command'] += "\necho changed"
    refute_equal checksum(payload), checksum(changed)
  end

  def test_unrelated_pod_is_untouched
    original = payload.merge('name' => 'AnotherPod')
    spec = store(original)
    assert_nil checksum(original)
    assert_equal Digest::SHA1.file(spec.defined_in_file).hexdigest, spec.checksum
    assert_equal original['source'], spec.attributes_hash['source']
  end

  def test_source_build_is_untouched
    original = payload.merge('source' => { 'git' => 'https://github.com/expo/expo.git' }, 'source_files' => 'ios/**/*.swift')
    original.delete('vendored_frameworks')
    original.delete('prepare_command')
    spec = store(original)
    assert_nil checksum(original)
    assert_equal Digest::SHA1.file(spec.defined_in_file).hexdigest, spec.checksum
  end

  def test_unexpected_archive_sources_fail_closed
    ['https://example.invalid/archive.tar.gz', 'file:///other/mobile/node_modules/expo-modules-core/prebuilds/output/debug/xcframeworks/ExpoModulesCore.tar.gz'].each do |location|
      changed = payload
      changed['source']['http'] = location
      assert_raises(ArgumentError) { checksum(changed) }
    end
    changed = payload
    changed['source']['extra'] = 'unknown'
    assert_raises(ArgumentError) { checksum(changed) }
  end

  def test_unexpected_preparation_shapes_fail_closed
    ['', 'TARBALL="/other/archive.tar.gz"', "#{payload['prepare_command']}\nTARBALL=\"duplicate\"", nil].each do |prepare|
      assert_raises(ArgumentError) { checksum(payload.merge('prepare_command' => prepare)) }
    end
  end

  def test_changed_settings_are_rejected_by_the_actual_deployment_lock_guard
    locked = installer(store(payload)).send(:generate_lockfile)
    changed = payload
    changed['pod_target_xcconfig']['OTHER_SWIFT_FLAGS'] += ' -DCHANGED'
    next_install = installer(store(changed), lockfile: locked)
    error = assert_raises(Pod::Informative) { next_install.send(:verify_no_lockfile_changes!) }
    assert_includes error.message, 'SPEC CHECKSUMS'
    assert_includes error.message, 'ExpoModulesCore'
  end
end

Minitest.after_run do
  results = ExpoCoreChecksumTest.completed_results
  unless results.length == ExpoCoreChecksumTest::EXPECTED_TESTS && results.map(&:name).sort == ExpoCoreChecksumTest.runnable_methods.sort && results.all?(&:passed?)
    abort 'The ExpoCore checksum regression suite did not complete all 16 tests without failures or skips.'
  end
end
