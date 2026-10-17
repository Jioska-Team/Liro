import { afterNextRender, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { InfrastructureService } from './core/api/infrastructure';
import { CatalogItem, BundleContents, CatalogService } from './core/api/catalog';
@Component({ selector: 'app-root', imports: [], templateUrl: './app.html', styleUrl: './app.css' })
export class App {
 private readonly catalog = inject(CatalogService);
 private readonly infrastructure = inject(InfrastructureService);
 private readonly destroyRef = inject(DestroyRef);
 protected readonly items = signal<CatalogItem[]>([]);
 protected readonly catalogLoading = signal(false);
 protected readonly catalogError = signal('');
 protected readonly page = signal(1);
 protected readonly hasMore = signal(false);
 protected readonly selectedBundle = signal<CatalogItem | null>(null);
 protected readonly contents = signal<BundleContents | null>(null);
 protected readonly componentsError = signal('');
 protected readonly connected = signal(false);
 constructor() {
  afterNextRender(() => {
  this.loadPage(1);
  this.infrastructure.getStatus().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
   next: () => this.connected.set(true), error: () => this.connected.set(false)
  });
  });
 }
 protected loadPage(page: number) {
  if (page < 1 || this.catalogLoading()) return;
  this.catalogLoading.set(true); this.catalogError.set(''); this.selectedBundle.set(null);
  this.catalog.getPage(page).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
   next: result => {
    this.items.set(result.items.filter(item => item.isCatalogVisible !== false &&
     (item.itemType === 'Bundle' || ![17,27,28,29,30,31,79].includes(item.assetTypeId ?? 0))));
    this.page.set(page); this.hasMore.set(result.hasMore); this.catalogLoading.set(false);
   },
   error: () => { this.catalogError.set('No se pudo cargar el catálogo. Inténtalo de nuevo.'); this.catalogLoading.set(false); }
  });
 }
 protected showComponents(item: CatalogItem) {
  this.selectedBundle.set(item); this.contents.set(null); this.componentsError.set('');
  this.catalog.getComponents(item.robloxItemId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
   next: result => { if (this.selectedBundle()?.id === item.id) this.contents.set(result); },
   error: () => { if (this.selectedBundle()?.id === item.id) this.componentsError.set('No se pudieron consultar los componentes.'); }
  });
 }
}
