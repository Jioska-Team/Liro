import { Component, inject, signal } from '@angular/core';
import { InfrastructureService } from './core/api/infrastructure';

@Component({
  selector: 'app-root',
  imports: [],
  templateUrl: './app.html',
  styleUrl: './app.css'
})
export class App {
  private readonly infrastructureService = inject(InfrastructureService);

  protected readonly loading = signal(true);
  protected readonly postgres = signal('checking...');
  protected readonly valkey = signal('checking...');
  protected readonly latency = signal<number | null>(null);
  protected readonly backendConnected = signal(false);

  constructor() {
    this.infrastructureService.getStatus().subscribe({
      next: status => {
        this.postgres.set(status.postgres);
        this.valkey.set(status.valkey);
        this.latency.set(status.valkeyLatencyMs);
        this.backendConnected.set(true);
        this.loading.set(false);
      },
      error: () => {
        this.backendConnected.set(false);
        this.loading.set(false);
      }
    });
  }
}