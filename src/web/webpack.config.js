const path = require('path');
const TerserPlugin = require('terser-webpack-plugin');
const CopyWebpackPlugin = require('copy-webpack-plugin');

module.exports = {
  entry: {
    'slick-ladder': './src/main.ts'
  },
  module: {
    rules: [
      {
        test: /\.tsx?$/,
        use: 'ts-loader',
        exclude: /node_modules/,
      },
    ],
  },
  resolve: {
    extensions: ['.tsx', '.ts', '.js'],
  },
  output: {
    filename: '[name].js',
    path: path.resolve(__dirname, 'dist'),
    clean: true,
    library: {
      name: 'SlickLadder',
      type: 'umd',
    },
  },
  plugins: [
    new CopyWebpackPlugin({
      patterns: [
        {
          from: 'wasm',
          to: 'wasm',
          noErrorOnMissing: true, // Don't fail if wasm folder doesn't exist yet
        },
      ],
    }),
  ],
  optimization: {
    minimizer: [
      new TerserPlugin({
        exclude: /dotnet.*\.js$/, // Don't minify .NET WASM runtime files
      }),
    ],
  },
  performance: {
    hints: false, // Disable performance warnings for WASM assets
    // Or configure specific limits:
    // maxAssetSize: 2000000, // 2MB
    // maxEntrypointSize: 2000000,
    // assetFilter: (assetFilename) => !assetFilename.endsWith('.wasm'),
  },
  devServer: {
    static: {
      directory: path.join(__dirname, 'dist'),
    },
    compress: true,
    port: 9000,
    hot: true,
    headers: {
      // Required for SharedArrayBuffer
      'Cross-Origin-Opener-Policy': 'same-origin',
      'Cross-Origin-Embedder-Policy': 'require-corp',
    },
  },
};
