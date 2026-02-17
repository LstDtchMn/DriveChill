import type { Metadata } from 'next';
import './globals.css';

export const metadata: Metadata = {
  title: 'DriveChill — Fan Controller',
  description: 'PC fan speed controller based on hardware temperatures',
};

export default function RootLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <html lang="en" className="dark">
      <body className="min-h-screen">
        {children}
      </body>
    </html>
  );
}
