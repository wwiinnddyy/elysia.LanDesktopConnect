export interface Capability {
  id: string;
  name: string;
  description: string;
  parameters?: Record<string, any>;
  returns?: any;
}

export interface HostCapability extends Capability {
  // 阑山桌面暴露的功能
  handler?: string; // 处理函数标识
}

export interface ExternalCapability extends Capability {
  // 外部应用暴露的功能
  appId: string;
}

export class CapabilityRegistry {
  private hostCapabilities = new Map<string, HostCapability>();
  private externalCapabilities = new Map<string, ExternalCapability>();

  // 注册阑山桌面的能力（从 C# 插件接收）
  registerHostCapability(capability: HostCapability): void {
    this.hostCapabilities.set(capability.id, capability);
    console.log(`[CapabilityRegistry] Host capability registered: ${capability.id}`);
  }

  // 注册外部应用的能力
  registerExternalCapability(capability: ExternalCapability): void {
    const key = `${capability.appId}:${capability.id}`;
    this.externalCapabilities.set(key, capability);
    console.log(`[CapabilityRegistry] External capability registered: ${key}`);
  }

  // 移除外部应用的所有能力
  removeExternalCapabilities(appId: string): void {
    for (const [key, cap] of this.externalCapabilities) {
      if (cap.appId === appId) {
        this.externalCapabilities.delete(key);
        console.log(`[CapabilityRegistry] External capability removed: ${key}`);
      }
    }
  }

  // 获取阑山桌面的能力
  getHostCapability(id: string): HostCapability | undefined {
    return this.hostCapabilities.get(id);
  }

  getHostCapabilities(): HostCapability[] {
    return Array.from(this.hostCapabilities.values());
  }

  // 获取外部应用的能力
  getExternalCapability(appId: string, id: string): ExternalCapability | undefined {
    const key = `${appId}:${id}`;
    return this.externalCapabilities.get(key);
  }

  getExternalCapabilities(): ExternalCapability[] {
    return Array.from(this.externalCapabilities.values());
  }

  // 查找特定应用的能力
  getExternalCapabilitiesByApp(appId: string): ExternalCapability[] {
    return Array.from(this.externalCapabilities.values())
      .filter(cap => cap.appId === appId);
  }

  // 查找所有提供特定能力 ID 的应用
  findAppsByCapabilityId(capabilityId: string): ExternalCapability[] {
    return Array.from(this.externalCapabilities.values())
      .filter(cap => cap.id === capabilityId);
  }

  // 清空所有能力
  clear(): void {
    this.hostCapabilities.clear();
    this.externalCapabilities.clear();
  }
}
