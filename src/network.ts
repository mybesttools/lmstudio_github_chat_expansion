import * as os from 'os';

function ipv4ToInt(ip: string): number | null {
  const match = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(ip);
  if (!match) {
    return null;
  }
  const octets = match.slice(1, 5).map(Number);
  if (octets.some((o) => o > 255)) {
    return null;
  }
  return ((octets[0] << 24) | (octets[1] << 16) | (octets[2] << 8) | octets[3]) >>> 0;
}

function expandEmbeddedIpv4(groups: string[]): string[] {
  if (groups.length === 0) {
    return groups;
  }
  const last = groups[groups.length - 1];
  if (!last.includes('.')) {
    return groups;
  }
  const asInt = ipv4ToInt(last);
  if (asInt === null) {
    return groups;
  }
  const hi = ((asInt >>> 16) & 0xffff).toString(16);
  const lo = (asInt & 0xffff).toString(16);
  return [...groups.slice(0, -1), hi, lo];
}

function ipv6ToBigInt(ip: string): bigint | null {
  const zonePos = ip.indexOf('%');
  const stripped = zonePos === -1 ? ip : ip.slice(0, zonePos);

  const halves = stripped.split('::');
  if (halves.length > 2) {
    return null;
  }

  const parseGroups = (part: string): string[] => (part.length === 0 ? [] : part.split(':'));

  const head = expandEmbeddedIpv4(parseGroups(halves[0]));
  const tail = halves.length === 2 ? expandEmbeddedIpv4(parseGroups(halves[1])) : [];

  let groups: string[];
  if (halves.length === 1) {
    groups = head;
  } else {
    const missing = 8 - (head.length + tail.length);
    if (missing < 0) {
      return null;
    }
    groups = [...head, ...Array(missing).fill('0'), ...tail];
  }

  if (groups.length !== 8) {
    return null;
  }

  let result = 0n;
  for (const group of groups) {
    if (!/^[0-9a-fA-F]{1,4}$/.test(group)) {
      return null;
    }
    result = (result << 16n) | BigInt(parseInt(group, 16));
  }
  return result;
}

/** True when `ip` falls inside `cidr` (e.g. "192.168.1.0/24" or "fd00::/8"). */
export function isIpInCidr(ip: string, cidr: string): boolean {
  const slashIndex = cidr.indexOf('/');
  const rangeIp = slashIndex === -1 ? cidr : cidr.slice(0, slashIndex);
  const prefixStr = slashIndex === -1 ? undefined : cidr.slice(slashIndex + 1);

  const ipv4 = ipv4ToInt(ip);
  const rangeIpv4 = ipv4ToInt(rangeIp);
  if (ipv4 !== null && rangeIpv4 !== null) {
    const prefix = prefixStr === undefined ? 32 : Number(prefixStr);
    if (!Number.isInteger(prefix) || prefix < 0 || prefix > 32) {
      return false;
    }
    const mask = prefix === 0 ? 0 : (~0 << (32 - prefix)) >>> 0;
    return (ipv4 & mask) === (rangeIpv4 & mask);
  }

  const ipv6 = ipv6ToBigInt(ip);
  const rangeIpv6 = ipv6ToBigInt(rangeIp);
  if (ipv6 !== null && rangeIpv6 !== null) {
    const prefix = prefixStr === undefined ? 128 : Number(prefixStr);
    if (!Number.isInteger(prefix) || prefix < 0 || prefix > 128) {
      return false;
    }
    const fullMask = (1n << 128n) - 1n;
    const mask = prefix === 0 ? 0n : (fullMask << BigInt(128 - prefix)) & fullMask;
    return (ipv6 & mask) === (rangeIpv6 & mask);
  }

  return false;
}

/** Non-internal IPv4/IPv6 addresses currently assigned to this machine's network interfaces. */
export function getLocalNetworkAddresses(): string[] {
  const interfaces = os.networkInterfaces();
  const addresses: string[] = [];
  for (const entries of Object.values(interfaces)) {
    for (const entry of entries ?? []) {
      if (!entry.internal) {
        addresses.push(entry.address);
      }
    }
  }
  return addresses;
}

/**
 * True when `homeSubnets` is empty (no restriction configured) or when at least one of this
 * machine's current non-internal addresses falls inside one of the configured CIDR ranges.
 */
export function isOnHomeNetwork(homeSubnets: string[], addresses: string[] = getLocalNetworkAddresses()): boolean {
  const subnets = homeSubnets.map((s) => s.trim()).filter(Boolean);
  if (subnets.length === 0) {
    return true;
  }
  return addresses.some((address) => subnets.some((subnet) => isIpInCidr(address, subnet)));
}

/**
 * True when `serverUrl` resolves to this machine's own loopback interface. A loopback server is
 * reachable regardless of which network the machine is on, so the home-network gate never applies
 * to it (otherwise a laptop-local LM Studio install would get needlessly gated on Wi-Fi location).
 */
export function isLocalhostUrl(serverUrl: string): boolean {
  let hostname: string;
  try {
    hostname = new URL(serverUrl).hostname;
  } catch {
    return false;
  }
  const bareHost = hostname.replace(/^\[/, '').replace(/\]$/, '').toLowerCase();
  return bareHost === 'localhost' || bareHost === '127.0.0.1' || bareHost === '::1';
}
