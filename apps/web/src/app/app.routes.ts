import { Routes } from '@angular/router';
import { StatusPage } from './status/status-page';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'identify' },
  {
    path: 'identify',
    title: 'Identify a plant · Anything Can Be Farming',
    loadComponent: () => import('./identify/identify-page').then(m => m.IdentifyPage),
  },
  { path: 'status', title: 'Status · Anything Can Be Farming', component: StatusPage },
  { path: '**', redirectTo: 'identify' },
];
