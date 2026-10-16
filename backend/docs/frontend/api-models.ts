/** JSON actual de Liro. Fechas ISO 8601; no son objetos Date al recibirlas. */
export type IsoDateTime = string;
export type Uuid = string;
/** El backend serializa este enum como numero. */
export enum ItemMarketStatus {
  Unknown = 0,
  Normal = 1,
  Limited = 2,
  LimitedUnique = 3,
  Collectible = 4,
}
export type Availability = 'OnSale' | 'OffSale' | 'NoSellers' | 'Unknown';
export interface ItemMarketData {
  primaryPriceRobux: number | null;
  priceBeforeDiscountRobux: number | null;
  lowestPriceRobux: number | null;
  lowestResalePriceRobux: number | null;
  isOffSale: boolean | null;
  hasResellers: boolean | null;
  isResellable: boolean | null;
  priceStatus: string | null;
  totalQuantity: number | null;
  unitsAvailableForConsumption: number | null;
  favoriteCount: number | null;
}
export interface ItemMarketResponse {
  data: ItemMarketData;
  observedAtUtc: IsoDateTime;
  source: string;
  availability: Availability;
  availablePriceRobux: number | null;
}
export interface ItemResponse {
  id: Uuid;
  robloxItemId: number;
  itemType: 'Asset' | 'Bundle';
  robloxAssetId: number | null;
  collectibleItemId: Uuid | null;
  name: string;
  marketStatus: ItemMarketStatus;
  thumbnailUrl: string | null;
  thumbnailUpdatedAt: IsoDateTime | null;
  firstSeenAt: IsoDateTime;
  lastUpdatedAt: IsoDateTime;
  lastCheckedAt: IsoDateTime | null;
  marketplaceUrl: string;
  description: string | null;
  creatorId: number | null;
  creatorType: string | null;
  creatorName: string | null;
  assetTypeId: number | null;
  bundleTypeId: number | null;
  restrictions: string[];
  robloxCreatedAtUtc: IsoDateTime | null;
  robloxUpdatedAtUtc: IsoDateTime | null;
  market: ItemMarketResponse | null;
}
export type ItemMarketHistoryResponse = ItemMarketResponse[];
export interface LanReadinessResponse {
  service: 'liro-api';
  status: 'ready' | 'unavailable';
}
/** No todos los errores tienen cuerpo JSON; comprobarlo antes de usar este modelo. */
export interface ApiProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  traceId?: string;
  errors?: Record<string, string[]>;
}
/** Contrato administrativo. No enviar desde el frontend LAN. */
export interface CreateItemRequest {
  robloxAssetId: number;
  name: string;
  collectibleItemId: Uuid | null;
  marketStatus: ItemMarketStatus;
}



