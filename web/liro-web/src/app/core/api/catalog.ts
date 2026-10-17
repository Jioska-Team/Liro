import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
export interface CatalogItem {
 id: string; robloxItemId: number; itemType: 'Asset' | 'Bundle'; name: string;
 assetTypeId: number | null; bundleTypeId: number | null; thumbnailUrl: string | null;
 marketplaceUrl: string; isCatalogVisible: boolean;
}
export interface CatalogPage { items: CatalogItem[]; page: number; pageSize: number; hasMore: boolean; }
export interface BundleContents { bundle: CatalogItem; components: CatalogItem[]; pendingAssetIds: number[]; relationshipsObservedAtUtc: string | null; }
@Injectable({ providedIn: 'root' })
export class CatalogService {
 private readonly http = inject(HttpClient);
 getPage(page: number) { return this.http.get<CatalogPage>('/api/v1/catalog', { params: { page, pageSize: 24 } }); }
 getComponents(id: number) { return this.http.get<BundleContents>(`/api/v1/catalog/bundles/${id}/components`); }
}
