require 'digest'
require 'json'
require 'uri'

# Expo's inline Core prebuild includes the checkout path in its saved podspec.
# Preserve all dependency inputs while keeping that location out of the lockfile.
module SolarExpoCoreChecksum
  CANONICAL_ROOT = '/__solar_checkout__/mobile'.freeze

  def self.checksum(json, mobile_root:)
    payload = JSON.parse(json)
    return nil unless payload['name'] == 'ExpoModulesCore'

    source = payload['source']
    return nil if source == { 'git' => 'https://github.com/expo/expo.git' } && payload.key?('source_files')

    unless source.is_a?(Hash) && source.keys.sort == %w[flatten http] && source['flatten'] == false
      raise ArgumentError, 'Unexpected ExpoModulesCore precompiled source shape'
    end

    root = File.expand_path(mobile_root.to_s)
    uri = URI.parse(source.fetch('http'))
    path = URI::DEFAULT_PARSER.unescape(uri.path.to_s)
    suffix = %r{\A#{Regexp.escape(root)}/(?<archive>node_modules/expo-modules-core/prebuilds/output/(?:debug|release)/xcframeworks/ExpoModulesCore\.tar\.gz)\z}.match(path)
    unless uri.scheme == 'file' && uri.host.to_s.empty? && uri.query.nil? && uri.fragment.nil? && suffix
      raise ArgumentError, 'Unexpected ExpoModulesCore precompiled archive location'
    end

    prepare = payload['prepare_command']
    assignment = /^[ \t]*TARBALL="#{Regexp.escape(path)}"$/
    unless prepare.is_a?(String) && prepare.scan(/^[ \t]*TARBALL=/).length == 1 && prepare.match?(assignment)
      raise ArgumentError, 'Unexpected ExpoModulesCore archive preparation command'
    end

    canonical_path = "#{CANONICAL_ROOT}/#{suffix[:archive]}"
    source['http'] = URI::File.build(path: canonical_path).to_s
    payload['prepare_command'] = prepare.sub(assignment) { |line| line.sub(path, canonical_path) }
    # Compact JSON also avoids Ruby JSON pretty-printer whitespace differences.
    Digest::SHA1.hexdigest(JSON.generate(payload))
  rescue URI::InvalidURIError, TypeError, KeyError
    raise ArgumentError, 'Unexpected ExpoModulesCore precompiled archive location'
  end

  module SandboxChecksum
    def store_podspec(name, podspec, external_source = false, json = false)
      SolarExpoCoreChecksum.apply(super, mobile_root: root.parent.parent)
    end

    def specification(name)
      SolarExpoCoreChecksum.apply(super, mobile_root: root.parent.parent)
    end
  end

  def self.apply(spec, mobile_root:)
    return spec unless spec && spec.name == 'ExpoModulesCore'

    checksum = checksum(spec.to_pretty_json, mobile_root: mobile_root)
    spec.instance_variable_set(:@checksum, checksum) if checksum
    spec
  end
end

Pod::Sandbox.prepend(SolarExpoCoreChecksum::SandboxChecksum)
