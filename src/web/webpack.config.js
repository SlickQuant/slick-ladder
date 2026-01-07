const path = require('path');
const HtmlWebpackPlugin = require('html-webpack-plugin');
const CopyWebpackPlugin = require('copy-webpack-plugin');
const TerserPlugin = require('terser-webpack-plugin');

module.exports = {
  entry: {
    'slick-ladder': './src/main.ts',
    'demo': './src/demo.ts'
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
    new HtmlWebpackPlugin({
      template: './public/index.html',
      filename: 'index.html',
    }),
    new CopyWebpackPlugin({
      patterns: [
        {
          from: 'public/wasm/*.wasm',
          to: 'wasm/[name][ext]',
          noErrorOnMissing: true
        },
        {
          from: 'public/wasm/*.dll',
          to: 'wasm/[name][ext]',
          noErrorOnMissing: true
        },
        {
          from: 'public/wasm/*.js',
          to: 'wasm/[name][ext]',
          noErrorOnMissing: true
        },
        {
          from: 'public/wasm/*.json',
          to: 'wasm/[name][ext]',
          noErrorOnMissing: true
        },
        {
          from: 'public/wasm/*.symbols',
          to: 'wasm/[name][ext]',
          noErrorOnMissing: true
        },
        {
          from: 'public/wasm/supportFiles/**/*',
          to: 'wasm/supportFiles/[name][ext]',
          noErrorOnMissing: true
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
