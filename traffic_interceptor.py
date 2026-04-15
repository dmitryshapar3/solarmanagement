#!/usr/bin/env python3
"""
Traffic Interceptor Script
Intercepts local traffic from a specific IP and searches for 'local_key' in JSON objects.

Requirements:
    pip install scapy

Usage:
    sudo python3 traffic_interceptor.py --target-ip <IP_ADDRESS>
    
Example:
    sudo python3 traffic_interceptor.py --target-ip 192.168.1.100
"""

import argparse
import json
import re
import sys
from collections import defaultdict
from datetime import datetime

try:
    from scapy.all import sniff, TCP, IP, Raw
except ImportError:
    print("Error: scapy is not installed. Please install it with: pip install scapy")
    sys.exit(1)


class TrafficInterceptor:
    """Intercepts and analyzes network traffic for specific JSON keys."""
    
    def __init__(self, target_ip: str, port: int = None, interface: str = None):
        """
        Initialize the traffic interceptor.
        
        Args:
            target_ip: The IP address to monitor
            port: Optional port filter (if None, monitors all ports)
            interface: Network interface to sniff on (if None, uses default)
        """
        self.target_ip = target_ip
        self.port = port
        self.interface = interface
        self.match_count = 0
        self.packet_count = 0
        self.matches = []
        
    def extract_json_from_data(self, data: bytes) -> list:
        """
        Extract JSON objects from raw data.
        
        Args:
            data: Raw bytes data
            
        Returns:
            List of extracted JSON objects
        """
        json_objects = []
        
        try:
            # Try to decode as UTF-8
            text = data.decode('utf-8', errors='ignore')
            
            # Find potential JSON objects using regex
            # This handles both {...} and [...] structures
            json_pattern = r'[\{\[][^\{\}\[\]]*(?:[\{\[][^\{\}\[\]]*[\]\}][^\{\}\[\]]*)*[\]\}]'
            
            # Simple approach: try to find JSON by looking for braces
            start_idx = 0
            while start_idx < len(text):
                # Find next potential JSON start
                for start_char in ['{', '[']:
                    start_pos = text.find(start_char, start_idx)
                    if start_pos == -1:
                        continue
                    
                    # Try to parse JSON starting from this position
                    for end_pos in range(start_pos + 1, len(text) + 1):
                        try:
                            candidate = text[start_pos:end_pos]
                            parsed = json.loads(candidate)
                            json_objects.append(parsed)
                            start_idx = end_pos
                            break
                        except (json.JSONDecodeError, ValueError):
                            continue
                    else:
                        continue
                    break
                else:
                    start_idx += 1
                    
        except Exception as e:
            pass
            
        return json_objects
    
    def search_for_local_key(self, data: dict or list, path: str = "") -> list:
        """
        Recursively search for 'local_key' in JSON structure.
        
        Args:
            data: JSON object (dict or list)
            path: Current path in the JSON structure
            
        Returns:
            List of tuples (path, value) where local_key was found
        """
        results = []
        
        if isinstance(data, dict):
            for key, value in data.items():
                current_path = f"{path}.{key}" if path else key
                
                if key == "local_key":
                    results.append((current_path, value))
                
                # Recurse into nested structures
                if isinstance(value, (dict, list)):
                    results.extend(self.search_for_local_key(value, current_path))
                    
        elif isinstance(data, list):
            for idx, item in enumerate(data):
                current_path = f"{path}[{idx}]"
                
                if isinstance(item, (dict, list)):
                    results.extend(self.search_for_local_key(item, current_path))
                    
        return results
    
    def packet_callback(self, packet):
        """
        Callback function for each captured packet.
        
        Args:
            packet: Scapy packet object
        """
        self.packet_count += 1
        
        # Check if packet has the required layers
        if not (IP in packet and TCP in packet and Raw in packet):
            return
            
        # Check if packet is from or to target IP
        ip_layer = packet[IP]
        if ip_layer.src != self.target_ip and ip_layer.dst != self.target_ip:
            return
            
        # Extract payload
        payload = packet[Raw].load
        
        # Try to extract and search JSON
        json_objects = self.extract_json_from_data(payload)
        
        for json_obj in json_objects:
            matches = self.search_for_local_key(json_obj)
            
            if matches:
                self.match_count += len(matches)
                
                # Determine direction
                direction = "RECEIVED" if ip_layer.src == self.target_ip else "SENT"
                
                match_info = {
                    'timestamp': datetime.now().strftime('%Y-%m-%d %H:%M:%S'),
                    'source_ip': ip_layer.src,
                    'dest_ip': ip_layer.dst,
                    'source_port': packet[TCP].sport,
                    'dest_port': packet[TCP].dport,
                    'direction': direction,
                    'matches': matches
                }
                
                self.matches.append(match_info)
                
                # Print match
                print(f"\n{'='*80}")
                print(f"[{match_info['timestamp']}] Found 'local_key' in {direction} traffic")
                print(f"Source: {ip_layer.src}:{packet[TCP].sport}")
                print(f"Destination: {ip_layer.dst}:{packet[TCP].dport}")
                print(f"{'-'*80}")
                
                for path, value in matches:
                    print(f"  Path: {path}")
                    print(f"  Value: {value}")
                    
                print(f"{'='*80}\n")
    
    def start_sniffing(self, count: int = 0, timeout: int = None):
        """
        Start sniffing packets.
        
        Args:
            count: Number of packets to capture (0 = infinite)
            timeout: Timeout in seconds (None = no timeout)
        """
        print(f"Starting traffic interception...")
        print(f"Target IP: {self.target_ip}")
        if self.port:
            print(f"Filter Port: {self.port}")
        print(f"Searching for: 'local_key' in JSON objects")
        print(f"Press Ctrl+C to stop\n")
        
        # Build BPF filter
        bpf_filter = f"host {self.target_ip}"
        if self.port:
            bpf_filter += f" and port {self.port}"
            
        try:
            sniff(
                filter=bpf_filter,
                prn=self.packet_callback,
                count=count,
                timeout=timeout,
                iface=self.interface,
                store=False  # Don't store packets in memory
            )
        except KeyboardInterrupt:
            print("\n\nInterception stopped by user.")
        except PermissionError:
            print("\nError: Permission denied. Try running with sudo.")
            sys.exit(1)
        finally:
            self.print_summary()
    
    def print_summary(self):
        """Print summary of intercepted traffic."""
        print(f"\n{'='*80}")
        print("TRAFFIC INTERCEPTION SUMMARY")
        print(f"{'='*80}")
        print(f"Total packets processed: {self.packet_count}")
        print(f"Total 'local_key' matches found: {self.match_count}")
        print(f"{'='*80}\n")


def main():
    parser = argparse.ArgumentParser(
        description='Intercept local traffic and search for local_key in JSON objects'
    )
    parser.add_argument(
        '--target-ip',
        type=str,
        required=True,
        help='Target IP address to monitor'
    )
    parser.add_argument(
        '--port',
        type=int,
        default=None,
        help='Optional port filter (default: all ports)'
    )
    parser.add_argument(
        '--interface',
        type=str,
        default=None,
        help='Network interface to sniff on (default: system default)'
    )
    parser.add_argument(
        '--count',
        type=int,
        default=0,
        help='Number of packets to capture (default: 0 = infinite)'
    )
    parser.add_argument(
        '--timeout',
        type=int,
        default=None,
        help='Timeout in seconds (default: None = no timeout)'
    )
    parser.add_argument(
        '--output',
        type=str,
        default=None,
        help='Output file for saving matches (JSON format)'
    )
    
    args = parser.parse_args()
    
    # Validate IP address
    import socket
    try:
        socket.inet_aton(args.target_ip)
    except socket.error:
        print(f"Error: Invalid IP address: {args.target_ip}")
        sys.exit(1)
    
    # Create interceptor
    interceptor = TrafficInterceptor(
        target_ip=args.target_ip,
        port=args.port,
        interface=args.interface
    )
    
    # Start sniffing
    interceptor.start_sniffing(count=args.count, timeout=args.timeout)
    
    # Save results if output file specified
    if args.output and interceptor.matches:
        try:
            with open(args.output, 'w') as f:
                json.dump(interceptor.matches, f, indent=2)
            print(f"Matches saved to: {args.output}")
        except Exception as e:
            print(f"Error saving output file: {e}")


if __name__ == '__main__':
    main()
