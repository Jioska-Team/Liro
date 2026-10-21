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

describe('Conexión y componentes', () => {
 const pageUrl = '/api/v1/catalog?page=1&pageSize=24';
 const bundle = (id: number) => ({
  id: 'bundle-' + id, robloxItemId: id, itemType: 'Bundle', name: 'Bundle ' + id,
  assetTypeId: null, bundleTypeId: 1, thumbnailUrl: null,
  marketplaceUrl: 'https://www.roblox.com/bundles/' + id, isCatalogVisible: true
 });
 const detail = (id: number) => ({
  bundle: bundle(id), components: [{ ...bundle(id + 10), name: 'Componente ' + id }],
  pendingAssetIds: [], relationshipsObservedAtUtc: '2026-10-17T00:00:00Z'
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
  const open = (index: number) => element.querySelectorAll<HTMLButtonElement>('article button')[index].click();
  return { fixture, http, element, open };
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
 it('RepeatedBundleClicksReusePendingAndLoadedDetails', async () => {
  const { fixture, http, open, element } = await setup();
  open(0); open(0);
  const request = http.expectOne('/api/v1/catalog/bundles/1/components');
  request.flush(detail(1));
  await fixture.whenStable();
  open(0);
  http.expectNone('/api/v1/catalog/bundles/1/components');
  expect(element.querySelector('.components')?.textContent).toContain('Componente 1');
 });
 it('ChangingBundleCancelsPreviousRequestAndDisplaysLatest', async () => {
  const { fixture, http, open, element } = await setup();
  open(0);
  const previous = http.expectOne('/api/v1/catalog/bundles/1/components');
  open(1);
  expect(previous.cancelled).toBe(true);
  http.expectOne('/api/v1/catalog/bundles/2/components').flush(detail(2));
  await fixture.whenStable();
  expect(element.querySelector('.components')?.textContent).toContain('Componente 2');
  expect(element.querySelector('.components')?.textContent).not.toContain('Componente 1');
 });
 it('ClosingAndReopeningCancelsOldRequestAndFetchesAgain', async () => {
  const { fixture, http, open, element } = await setup();
  open(0);
  const previous = http.expectOne('/api/v1/catalog/bundles/1/components');
  await fixture.whenStable();
  element.querySelector<HTMLButtonElement>('.components button')!.click();
  expect(previous.cancelled).toBe(true);
  await fixture.whenStable();
  expect(element.querySelector('.components')).toBeNull();
  open(0);
  http.expectOne('/api/v1/catalog/bundles/1/components').flush(detail(1));
  await fixture.whenStable();
  expect(element.querySelector('.components')?.textContent).toContain('Componente 1');
 });
 it('FailedComponentsCanBeRetried', async () => {
  const { fixture, http, open, element } = await setup();
  open(0);
  http.expectOne('/api/v1/catalog/bundles/1/components').flush({}, { status: 503, statusText: 'Unavailable' });
  await fixture.whenStable();
  expect(element.querySelector('.components [role="alert"]')).not.toBeNull();
  open(0);
  http.expectOne('/api/v1/catalog/bundles/1/components').flush(detail(1));
  await fixture.whenStable();
  expect(element.querySelector('.components [role="alert"]')).toBeNull();
  expect(element.querySelector('.components')?.textContent).toContain('Componente 1');
 });
 it('ChangingPageCancelsComponentRequest', async () => {
  const { fixture, http, open, element } = await setup();
  open(0);
  const previous = http.expectOne('/api/v1/catalog/bundles/1/components');
  element.querySelectorAll<HTMLButtonElement>('nav button')[1].click();
  expect(previous.cancelled).toBe(true);
  http.expectOne('/api/v1/catalog?page=2&pageSize=24').flush({ items: [], page: 2, pageSize: 24, hasMore: false });
  await fixture.whenStable();
  expect(element.querySelector('.components')).toBeNull();
 });
 it('DestroyingComponentCancelsPendingRequest', async () => {
  const { fixture, http, open } = await setup();
  open(0);
  const pending = http.expectOne('/api/v1/catalog/bundles/1/components');
  fixture.destroy();
  expect(pending.cancelled).toBe(true);
 });
});
