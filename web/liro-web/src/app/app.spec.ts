import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { App } from './app';

describe('Catálogo', () => {
 beforeEach(() => TestBed.configureTestingModule({ imports: [App], providers: [provideHttpClient(), provideHttpClientTesting()] }));
 afterEach(() => {
  try { TestBed.inject(HttpTestingController).verify(); }
  finally { TestBed.resetTestingModule(); }
 });
 it('muestra el avatar y el accesorio, oculta la pierna y consulta los componentes', async () => {
  const fixture = TestBed.createComponent(App);
  fixture.detectChanges();
  await fixture.whenStable();
  const http = TestBed.inject(HttpTestingController);
  const base = {thumbnailUrl:null, marketplaceUrl:'https://www.roblox.com/bundles/1', isCatalogVisible:true, bundleTypeId:1};
  const avatar = {...base,id:'bundle',robloxItemId:1,itemType:'Bundle',name:'Avatar completo de prueba',assetTypeId:null};
  http.expectNone('/api/infrastructure');
  http.expectOne('/api/v1/catalog?page=1&pageSize=24').flush({items:[avatar,
   {...base,id:'leg',robloxItemId:2,itemType:'Asset',name:'Pierna oculta',assetTypeId:30},
   {...base,id:'hat',robloxItemId:3,itemType:'Asset',name:'Sombrero visible',assetTypeId:8}],page:1,pageSize:24,hasMore:false});
  fixture.detectChanges();
  const element = fixture.nativeElement as HTMLElement;
  expect(element.querySelectorAll('article').length).toBe(2);
  expect(element.textContent).toContain('Sombrero visible');
  expect(element.textContent).not.toContain('Pierna oculta');
  element.querySelector<HTMLButtonElement>('article button')!.click();
  http.expectOne('/api/v1/catalog/bundles/1/components').flush({bundle:avatar,components:[{...base,id:'leg',name:'Pierna del avatar'}],pendingAssetIds:[],relationshipsObservedAtUtc:'2026-10-17T00:00:00Z'});
  fixture.detectChanges();
  expect(element.querySelector('.components')?.textContent).toContain('Pierna del avatar');
  expect(element.querySelectorAll('article').length).toBe(2);
 });
});

describe('Conexión', () => {
 const pageUrl = '/api/v1/catalog?page=1&pageSize=24';
 const bundle = (id: number) => ({
  id: 'bundle-' + id, robloxItemId: id, itemType: 'Bundle', name: 'Bundle ' + id,
  assetTypeId: null, bundleTypeId: 1, thumbnailUrl: null,
  marketplaceUrl: 'https://www.roblox.com/bundles/' + id, isCatalogVisible: true
 });
 beforeEach(() => TestBed.configureTestingModule({
  imports: [App], providers: [provideHttpClient(), provideHttpClientTesting()]
 }));
 afterEach(() => {
  try { TestBed.inject(HttpTestingController).verify(); }
  finally { TestBed.resetTestingModule(); }
 });
 async function setup(load = true) {
  const fixture = TestBed.createComponent(App);
  await fixture.whenStable();
  const http = TestBed.inject(HttpTestingController);
  http.expectNone('/api/infrastructure');
  if (load) {
   http.expectOne(pageUrl).flush({ items: [bundle(1), bundle(2)], page: 1, pageSize: 24, hasMore: true });
   await fixture.whenStable();
  }
  const element = fixture.nativeElement as HTMLElement;
  return { fixture, http, element };
 }
 it('CatalogSuccessReportsConnectedWithoutDevelopmentDiagnostics', async () => {
  const { element } = await setup();
  expect(element.querySelector('details')?.textContent).toContain('Servidor conectado');
 });
 it('CatalogFailureAndRetryUpdateConnection', async () => {
  const { fixture, http, element } = await setup(false);
  http.expectOne(pageUrl).error(new ProgressEvent('error'));
  await fixture.whenStable();
  expect(element.querySelector('details')?.textContent).toContain('Servidor sin conexión');
  element.querySelector<HTMLButtonElement>('main > button')!.click();
  http.expectOne(pageUrl).flush({ items: [], page: 1, pageSize: 24, hasMore: false });
  await fixture.whenStable();
  expect(element.querySelector('details')?.textContent).toContain('Servidor conectado');
 });
});
